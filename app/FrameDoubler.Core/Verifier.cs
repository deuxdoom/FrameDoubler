using System.Text.Json;

namespace FrameDoubler.Core;

public sealed record VerificationReport(Rational Fps,Rational ExpectedFps,long Frames,double DurationDifference,
    double DuplicateRatio,double InputDuplicateRatio,double MaximumSyncError,List<string> Problems,
    double EvaluatedDuplicateRatio=0,int ExpectedTerminalHolds=0)
{public bool Ok=>Problems.Count==0;}

public static class Verifier
{
    public static async Task<VerificationReport> VerifyAsync(AppPaths paths,string input,string output,Rational? expected=null,CancellationToken cancel=default)
    {
        var source=await MediaProbe.ReadAsync(paths,input,cancel);var dest=await MediaProbe.ReadAsync(paths,output,cancel);
        Rational fps=expected??Timeline.DefaultRate(source.IsVfr?source.NominalFps:source.Fps);
        var before=await CountAsync(paths,source,1,cancel);
        float threshold=(float)Math.Min(1,60/dest.Fps.ToDouble());
        var after=await CountAsync(paths,dest,threshold,cancel);
        var problems=new List<string>();double tolerance=1.01/fps.ToDouble();
        // No future source frame exists after the final retained frame. The
        // timeline deliberately holds it through the source duration. Exempt
        // only actual trailing duplicates within that exact unavoidable span.
        int expectedHolds=(int)Math.Max(0,((source.Duration-before.LastKept)*dest.Fps).Ceiling()-1);
        int ignored=Math.Min(expectedHolds,after.TrailingDuplicates);
        double evaluated=(double)Math.Max(0,after.Duplicate-ignored)/Math.Max(1,after.Unique+after.Duplicate-ignored);
        double difference=(dest.Duration-source.Duration).ToDouble();
        if(dest.Fps!=fps)problems.Add($"Frame rate {dest.Fps}, expected {fps}");
        if(Math.Abs(difference)>tolerance)problems.Add($"Duration changed by {difference*1000:F2}ms");
        foreach(var (name,a,b) in new[]{("matrix",source.ColorSpace,dest.ColorSpace),("primaries",source.ColorPrimaries,dest.ColorPrimaries),
            ("transfer",source.ColorTransfer,dest.ColorTransfer),("range",source.ColorRange,dest.ColorRange)})
            if(a is not (null or "" or "unknown" or "unspecified")&&a!=b)problems.Add($"Color {name} changed: {a} -> {b}");
        if(evaluated>.01&&(before.Unique>1||after.Unique>1||after.Static==0))problems.Add($"{evaluated:P2} of moving frames remain duplicates (excluding {ignored} required terminal holds)");
        if(source.Audio.Length!=dest.Audio.Length)problems.Add("Audio track count changed");
        var inAudio=await AudioAsync(paths,source,cancel);var outAudio=await AudioAsync(paths,dest,cancel);
        double maxError=0;
        Rational videoShift=dest.Start-source.Start;
        for(int track=0;track<Math.Min(inAudio.Length,outAudio.Length);track++){
            var a=inAudio[track];var b=outAudio[track];
            if(a.Count==0||b.Count==0){problems.Add("Missing decoded audio frames");continue;}
            var durationA=a.Aggregate((Rational)0,(v,s)=>v+s.Length);var durationB=b.Aggregate((Rational)0,(v,s)=>v+s.Length);
            if(Math.Abs((durationB-durationA).ToDouble())>tolerance)problems.Add("Audio duration changed");
            foreach(var portion in new[]{(Rational)0,new Rational(1,4),new Rational(1,2),new Rational(3,4),(Rational)1}){
                var error=At(b,durationB*portion)-At(a,durationA*portion)-videoShift;
                maxError=Math.Max(maxError,Math.Abs(error.ToDouble()));
            }
        }
        if(maxError>tolerance)problems.Add($"Audio/video synchronization changed by {maxError*1000:F2}ms");
        return new(dest.Fps,fps,dest.Pts.Length,difference,after.Ratio,before.Ratio,maxError,problems,evaluated,ignored);
    }
    private static async Task<(int Unique,int Duplicate,int Static,double Ratio,Rational LastKept,int TrailingDuplicates)> CountAsync(AppPaths paths,MediaInfo info,float scale,CancellationToken cancel){
        var detector=new CadenceDetector(4*scale,.5f*scale,.5f*scale);int unique=0,duplicate=0,still=0;
        Rational pending=0,lastKept=0;int trailing=0;
        void Count(FrameLabel? label){
            if(!label.HasValue)return;
            if(label==FrameLabel.Unique)unique++;else if(label==FrameLabel.Duplicate)duplicate++;else still++;
            if(label==FrameLabel.Duplicate)trailing++;else{trailing=0;lastKept=pending;}
        }
        await foreach(var frame in Decoder.FramesAsync(paths,info,cancel:cancel)){using(frame){Count(detector.Push(Signature.Create(frame,info)));pending=frame.Time;}}
        Count(detector.Flush());return(unique,duplicate,still,(double)duplicate/Math.Max(1,unique+duplicate),lastKept,trailing);
    }
    private sealed record Segment(Rational Start,Rational Length);
    private static async Task<List<Segment>[]> AudioAsync(AppPaths paths,MediaInfo info,CancellationToken cancel){
        if(info.Audio.Length==0)return [];
        var run=await ProcessRunner.RunAsync(paths.FFprobe,["-v","error","-select_streams","a","-show_frames",
            "-show_entries","frame=stream_index,pts,nb_samples,sample_rate","-of","json",info.Path],cancel,TimeSpan.FromMinutes(5));
        if(run.ExitCode!=0)throw new IOException(run.Error);
        using var doc=JsonDocument.Parse(run.Output);
        var result=info.Audio.Select(_=>new List<Segment>()).ToArray();
        Rational origin=info.Audio.Select(x=>x.Start).Append(info.Start).Min();
        for(int i=0;i<info.Audio.Length;i++)if(info.Audio[i].Start>origin)result[i].Add(new(origin,info.Audio[i].Start-origin));
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
            if(end>start)result[track].Add(new(start,end-start));
        }
        return result;
    }
    private static Rational At(List<Segment> segments,Rational offset){
        foreach(var item in segments){if(offset<=item.Length)return item.Start+offset;offset-=item.Length;}
        var last=segments[^1];return last.Start+last.Length+offset;
    }
}
