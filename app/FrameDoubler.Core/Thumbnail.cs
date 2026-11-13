namespace FrameDoubler.Core;

// A representative still for the queue. FFmpeg's thumbnail filter picks the frame closest to the
// average histogram of a short run, which skips black or flash frames; HDR is tone-mapped first.
public static class Thumbnail
{
    public static string Filter(MediaInfo info,int width)=>
        (info.HdrInput is { } hdr?ToneMap.Filter(hdr)+",":"")+$"thumbnail=n=24,scale={width}:-2:flags=lanczos,format=rgb24";
    public static double Position(MediaInfo info){
        double duration=info.Duration.ToDouble();
        return duration<4?0:Math.Max(0,Math.Min(duration/3,duration-3));
    }
    public static async Task<byte[]> CaptureAsync(AppPaths paths,MediaInfo info,int width=256,CancellationToken cancel=default){
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(cancel);limit.CancelAfter(TimeSpan.FromSeconds(30));
        var start=ProcessRunner.StartInfo(paths.FFmpeg,["-nostdin","-v","error","-ss",Position(info).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture),
            "-i",info.Path,"-map","0:v:0","-frames:v","1","-vf",Filter(info,width),"-f","image2pipe","-c:v","png","-"]);
        using var process=ProcessRunner.Start(start,"thumbnail");
        using var kill=limit.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
        var errors=process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var image=new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(image,limit.Token);
        await process.WaitForExitAsync(CancellationToken.None);
        cancel.ThrowIfCancellationRequested();
        if(process.ExitCode!=0||image.Length==0)throw new IOException("Thumbnail failed: "+(await errors).Trim());
        return image.ToArray();
    }
}
