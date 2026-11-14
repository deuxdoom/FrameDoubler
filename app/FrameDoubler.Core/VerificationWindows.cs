using System.Text.Json;
using System.Runtime.CompilerServices;

namespace FrameDoubler.Core;

public sealed record VerificationWindow(Rational Start,Rational Duration){public Rational End=>Start+Duration;}

public static class VerificationWindows
{
    // Relative to the first video timestamp. Merge overlaps instead of scanning short clips three times.
    public static VerificationWindow[] Plan(Rational duration){
        if(duration<=0)return [];
        Rational length=duration<60?duration:60;
        var starts=new[]{(Rational)0,(duration-length)/2,duration-length};
        var result=new List<VerificationWindow>();
        foreach(var start in starts){
            if(result.Count>0&&start<=result[^1].End){var last=result[^1];result[^1]=new(last.Start,start+length-last.Start);}
            else result.Add(new(start,length));
        }
        return result.ToArray();
    }
    public static double Seconds(Rational duration)=>Plan(duration).Sum(x=>x.Duration.ToDouble());

    internal static async Task<MediaInfo> ReadInfoAsync(AppPaths paths,string path,CancellationToken cancel){
        var info=await MediaProbe.ReadAsync(paths,path,cancel,metadataOnly:true);
        // Some older MP4 files report duration only up to the last frame's PTS.
        // Read the bounded tail's packet duration as well, just as the conversion
        // timeline does, without enumerating the whole file.
        var tail=new VerificationWindow(info.Duration>2?info.Duration-2:0,2);
        var probe=await ProcessRunner.RunAsync(paths.FFprobe,["-v","error","-select_streams","v:0","-read_intervals",Interval(info,tail),
            "-show_packets","-show_entries","packet=pts,duration","-of","json",path],cancel);
        if(probe.ExitCode!=0)throw new IOException(probe.Error);
        using var json=JsonDocument.Parse(probe.Output);
        var packets=json.RootElement.GetProperty("packets").EnumerateArray().Where(x=>x.TryGetProperty("pts",out _))
            .Select(x=>(Pts:x.GetProperty("pts").GetInt64(),Duration:x.TryGetProperty("duration",out var d)?d.GetInt64():0)).OrderBy(x=>x.Pts).ToArray();
        if(packets.Length==0)throw new IOException("Cannot inspect final presentation timestamps");
        Rational last=packets[^1].Duration>0?packets[^1].Duration*info.TimeBase:
            packets.Length>1?packets.Zip(packets.Skip(1),(a,b)=>b.Pts-a.Pts).Where(x=>x>0).DefaultIfEmpty(1).Min()*info.TimeBase:(Rational)1/info.Fps;
        Rational duration=packets[^1].Pts*info.TimeBase-info.Start+last;
        if(duration<=0)throw new IOException("Invalid sampled video duration");
        return info with{Duration=duration};
    }

    internal static string Interval(MediaInfo info,VerificationWindow window)=>
        // Do not seek at the first timestamp: MP4 AAC priming can otherwise discard
        // the first audible frame. Interior windows get a bounded audio/reorder pre-roll.
        (window.Start==0?"":(info.Start+window.Start-1).Seconds())+"%"+(info.Start+window.End+1).Seconds();

    // Bounded presentation timestamps, including keyframe pre-roll / a small reorder tail.
    // Sorting packets avoids an extra full-resolution decode just to learn PTS.
    // Only timestamps in the requested window enter the scan. No full-file packet listing.
    internal static async Task<long[]> PtsAsync(AppPaths paths,MediaInfo info,VerificationWindow window,CancellationToken cancel){
        var probe=await ProcessRunner.RunAsync(paths.FFprobe,["-v","error","-select_streams","v:0","-read_intervals",Interval(info,window),
            "-show_packets","-show_entries","packet=pts","-of","json",info.Path],cancel,TimeSpan.FromMinutes(5));
        if(probe.ExitCode!=0)throw new IOException(probe.Error);
        using var json=JsonDocument.Parse(probe.Output);
        Rational start=info.Start+window.Start,end=info.Start+window.End;
        var pts=json.RootElement.GetProperty("packets").EnumerateArray().Where(x=>x.TryGetProperty("pts",out _))
            .Select(x=>x.GetProperty("pts").GetInt64())
            .Where(x=>(Rational)x*info.TimeBase>=start&&(Rational)x*info.TimeBase<end).Order().ToArray();
        if(pts.Zip(pts.Skip(1),(a,b)=>b-a).Any(x=>x<=0))throw new IOException("Non-increasing sampled presentation timestamps");
        return pts;
    }
    internal static async IAsyncEnumerable<VideoFrame> FramesAsync(AppPaths paths,MediaInfo info,VerificationWindow window,long[] pts,
        [EnumeratorCancellation]CancellationToken cancel){
        using var process=ProcessRunner.Start(ProcessRunner.StartInfo(paths.FFmpeg,["-nostdin","-v","error","-seek_timestamp","1",
            "-ss",(info.Start+window.Start).Seconds(),"-i",info.Path,"-t",window.Duration.Seconds(),"-map","0:v:0",
            ..ToneMap.Arguments(info),"-frames:v",pts.Length.ToString(),"-fps_mode","passthrough","-f","rawvideo","-pix_fmt",info.BitDepth>8?"rgb48le":"rgb24","-"]),"verification sample decoder");
        var errors=process.StandardError.ReadToEndAsync();
        using var kill=cancel.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){} });
        try{
            for(int i=0;i<pts.Length;i++){
                Rational time=(Rational)pts[i]*info.TimeBase-info.Start;
                VideoFrame? frame=new(info.FrameBytes,checked((int)(time*info.Fps).Ceiling()),time);
                try{await process.StandardOutput.BaseStream.ReadExactlyAsync(frame.Memory,cancel);var result=frame;frame=null;yield return result;}
                finally{frame?.Dispose();}
            }
        }finally{
            try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}
            await process.WaitForExitAsync();await errors;
        }
    }
}
