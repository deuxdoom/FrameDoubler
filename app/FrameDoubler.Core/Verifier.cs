using System.Text.Json;

namespace FrameDoubler.Core;

public sealed record VerificationReport(Rational Fps,Rational ExpectedFps,long Frames,double DurationDifference,
    double DuplicateRatio,double InputDuplicateRatio,double MaximumSyncError,List<string> Problems,
    double EvaluatedDuplicateRatio=0,int ExpectedTerminalHolds=0,int ExpectedContentHolds=0)
{
    public bool Ok=>Problems.Count==0;
    public VerificationWindow[] Windows{get;init;}=[];
    public long InspectedFrames{get;init;}
    public bool FrameCountEstimated{get;init;}
    public double InspectedSeconds=>Windows.Sum(x=>x.Duration.ToDouble());
    public bool Partial{get;init;}
}

public static class Verifier
{
    public static async Task<VerificationReport> VerifyAsync(AppPaths paths,string input,string output,Rational? expected=null,CancellationToken cancel=default)
    {
        var source=await VerificationWindows.ReadInfoAsync(paths,input,cancel);var dest=await VerificationWindows.ReadInfoAsync(paths,output,cancel);
        Rational fps=expected??Timeline.DefaultRate(source.IsVfr?source.NominalFps:source.Fps);
        float threshold=(float)Math.Min(1,60/dest.Fps.ToDouble());
        var problems=new List<string>();double tolerance=1.01/fps.ToDouble();
        var windows=VerificationWindows.Plan(source.Duration<dest.Duration?source.Duration:dest.Duration);
        var before=new Scan(0,0,0,0,0,[],[]);var after=before;int ignored=0,contentHolds=0;long inspected=0;
        foreach(var window in windows){
            var a=await CountAsync(paths,source,window,1,cancel,dest.Fps);
            var b=await CountAsync(paths,dest,window,threshold,cancel);
            if(a.Unique+a.Duplicate+a.Static==0||b.Unique+b.Duplicate+b.Static==0)problems.Add("Missing decoded video sample");
            before=Add(before,a);after=Add(after,b);inspected+=b.Unique+b.Duplicate+b.Static;
            // Only the final window can contain unavoidable terminal holds.
            int expectedHolds=window.End>=source.Duration?(int)Math.Max(0,((source.Duration-a.LastKept)*dest.Fps).Ceiling()-1):0;
            int terminal=Math.Min(expectedHolds,b.TrailingDuplicates);ignored+=terminal;
            // Exempt only actual duplicates at source-proven hold positions.
            contentHolds+=b.DuplicateIndices.Count(x=>x<dest.FrameCount-terminal&&a.ContentHolds.Contains(x));
        }
        double evaluated=(double)Math.Max(0,after.Duplicate-ignored-contentHolds)/Math.Max(1,after.Unique+after.Duplicate-ignored-contentHolds);
        double difference=(dest.Duration-source.Duration).ToDouble();
        if(dest.Fps!=fps)problems.Add($"Frame rate {dest.Fps}, expected {fps}");
        if(Math.Abs(difference)>tolerance)problems.Add($"Duration changed by {difference*1000:F2}ms");
        foreach(var (name,a,b) in new[]{("matrix",source.ColorSpace,dest.ColorSpace),("primaries",source.ColorPrimaries,dest.ColorPrimaries),
            ("transfer",source.ColorTransfer,dest.ColorTransfer),("range",source.ColorRange,dest.ColorRange)})
            if(a is not (null or "" or "unknown" or "unspecified")&&a!=b)problems.Add($"Color {name} changed: {a} -> {b}");
        if(evaluated>.01&&(before.Unique>1||after.Unique>1||after.Static==0))problems.Add($"{evaluated:P2} of moving frames remain duplicates (excluding {ignored} terminal and {contentHolds} scene/static holds)");
        if(source.Audio.Length!=dest.Audio.Length)problems.Add("Audio track count changed");
        double maxError=0;
        Rational videoShift=dest.Start-source.Start;
        foreach(var window in windows){
        var inAudio=await AudioAsync(paths,source,window,cancel);var outAudio=await AudioAsync(paths,dest,window,cancel);
        for(int track=0;track<Math.Min(inAudio.Length,outAudio.Length);track++){
            var a=inAudio[track];var b=outAudio[track];
            if(a.Count==0&&b.Count==0)continue;
            if(a.Count==0||b.Count==0){problems.Add("Missing decoded audio frames in sample");continue;}
            var durationA=a.Aggregate((Rational)0,(v,s)=>v+s.Length);var durationB=b.Aggregate((Rational)0,(v,s)=>v+s.Length);
            if(Math.Abs((durationB-durationA).ToDouble())>tolerance)problems.Add("Audio duration changed");
            foreach(var portion in new[]{(Rational)0,new Rational(1,4),new Rational(1,2),new Rational(3,4),(Rational)1}){
                var error=At(b,durationB*portion)-At(a,durationA*portion)-videoShift;
                maxError=Math.Max(maxError,Math.Abs(error.ToDouble()));
            }
        }
        }
        for(int track=0;track<Math.Min(source.Audio.Length,dest.Audio.Length);track++)
            if(source.Audio[track].Duration is {} ad&&dest.Audio[track].Duration is {} bd){
                Rational aEnd=source.Audio[track].Start+ad-source.Start,bEnd=dest.Audio[track].Start+bd-dest.Start;
                if(Math.Abs((bEnd-aEnd).ToDouble())>tolerance)problems.Add("Audio end time changed");
            }
        if(maxError>tolerance)problems.Add($"Audio/video synchronization changed by {maxError*1000:F2}ms");
        return new(dest.Fps,fps,dest.FrameCount,difference,after.Ratio,before.Ratio,maxError,problems.Distinct().ToList(),evaluated,ignored,contentHolds){
            Windows=windows,InspectedFrames=inspected,FrameCountEstimated=dest.ReportedFrames is null,Partial=windows.Sum(x=>x.Duration.ToDouble())<source.Duration.ToDouble()-.001};
    }
    private sealed record Scan(int Unique,int Duplicate,int Static,Rational LastKept,int TrailingDuplicates,HashSet<long> DuplicateIndices,HashSet<long> ContentHolds){public double Ratio=>(double)Duplicate/Math.Max(1,Unique+Duplicate);}
    private static Scan Add(Scan a,Scan b)=>new(a.Unique+b.Unique,a.Duplicate+b.Duplicate,a.Static+b.Static,b.LastKept,b.TrailingDuplicates,[],[]);
    private static async Task<Scan> CountAsync(AppPaths paths,MediaInfo info,VerificationWindow window,float scale,CancellationToken cancel,Rational? outputFps=null){
        var detector=new CadenceDetector(4*scale,.5f*scale,.5f*scale);int unique=0,duplicate=0,still=0;
        Rational pending=0,lastKept=0;int trailing=0;
        int pendingIndex=0;Signature? pendingSignature=null;float[]? leftScene=null;Rational leftTime=0;FrameLabel leftLabel=FrameLabel.Unique;
        var duplicateIndices=new HashSet<long>();var contentHolds=new HashSet<long>();
        void Count(FrameLabel? label){
            if(!label.HasValue)return;
            if(label==FrameLabel.Unique)unique++;else if(label==FrameLabel.Duplicate)duplicate++;else still++;
            if(label==FrameLabel.Duplicate)duplicateIndices.Add(pendingIndex);
            if(outputFps is {} fps&&label!=FrameLabel.Duplicate){
                float[] scene=pendingSignature!.Descriptor();
                bool hold=label==FrameLabel.Static||leftScene is not null&&Signature.CutScore(leftScene,scene)>.35f;
                if(leftScene is not null)foreach(var (index,position) in Timeline.Steps(leftTime,pending,fps)){
                    if(position==0&&leftLabel==FrameLabel.Static||position>0&&position<1&&hold)contentHolds.Add(index);
                }
                leftScene=scene;leftTime=pending;leftLabel=label.Value;
            }
            if(label==FrameLabel.Duplicate)trailing++;else{trailing=0;lastKept=pending;}
        }
        var pts=await VerificationWindows.PtsAsync(paths,info,window,cancel);
        await foreach(var frame in VerificationWindows.FramesAsync(paths,info,window,pts,cancel)){using(frame){var signature=Signature.Create(frame,info);Count(detector.Push(signature));pending=frame.Time;pendingSignature=signature;pendingIndex=frame.Index;}}
        Count(detector.Flush());return new(unique,duplicate,still,lastKept,trailing,duplicateIndices,contentHolds);
    }
    private sealed record Segment(Rational Start,Rational Length);
    private static async Task<List<Segment>[]> AudioAsync(AppPaths paths,MediaInfo info,VerificationWindow window,CancellationToken cancel){
        if(info.Audio.Length==0)return [];
        var run=await ProcessRunner.RunAsync(paths.FFprobe,["-v","error","-select_streams","a","-read_intervals",VerificationWindows.Interval(info,window),"-show_frames",
            "-show_entries","frame=stream_index,pts,nb_samples,sample_rate","-of","json",info.Path],cancel,TimeSpan.FromMinutes(5));
        if(run.ExitCode!=0)throw new IOException(run.Error);
        using var doc=JsonDocument.Parse(run.Output);
        var result=info.Audio.Select(_=>new List<Segment>()).ToArray();
        Rational origin=info.Audio.Select(x=>x.Start).Append(info.Start).Min();
        Rational low=info.Start+window.Start,high=info.Start+window.End;
        void Add(int track,Rational start,Rational end){if(start<low)start=low;if(end>high)end=high;if(end>start)result[track].Add(new(start,end-start));}
        for(int i=0;i<info.Audio.Length;i++)if(info.Audio[i].Start>origin)Add(i,origin,info.Audio[i].Start);
        foreach(var frame in doc.RootElement.GetProperty("frames").EnumerateArray()){
            int stream=frame.GetProperty("stream_index").GetInt32();int track=Array.FindIndex(info.Audio,x=>x.Index==stream);
            if(track<0||!frame.TryGetProperty("pts",out var pts))continue;
            int samples=frame.TryGetProperty("nb_samples",out var ns)?ns.GetInt32():0;
            int rate=frame.TryGetProperty("sample_rate",out var sr)?int.Parse(sr.ToString()):info.Audio[track].SampleRate;
            if(samples<=0||rate<=0)continue;
            Rational start=(Rational)pts.GetInt64()*info.Audio[track].TimeBase;
            Rational end=start+new Rational(samples,rate);
            if(start<info.Audio[track].Start)start=info.Audio[track].Start;
            if(info.Audio[track].Duration is { } duration&&end>info.Audio[track].Start+duration)end=info.Audio[track].Start+duration;
            Add(track,start,end);
        }
        return result;
    }
    private static Rational At(List<Segment> segments,Rational offset){
        foreach(var item in segments){if(offset<=item.Length)return item.Start+offset;offset-=item.Length;}
        var last=segments[^1];return last.Start+last.Length+offset;
    }
}
