using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FrameDoubler.Core;

public static class Decoder
{
    public static async IAsyncEnumerable<VideoFrame> FramesAsync(AppPaths paths,MediaInfo info,int? maxFrames=null,
        [EnumeratorCancellation] CancellationToken cancel=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var channel=Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(2){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
        var produce=ProduceAsync();
        try { await foreach(var frame in channel.Reader.ReadAllAsync(cancel))yield return frame; }
        finally { linked.Cancel();try{await produce;}catch(OperationCanceledException){}finally{while(channel.Reader.TryRead(out var extra))extra.Dispose();} }

        async Task ProduceAsync() {
            using var process=new Process{StartInfo=ProcessRunner.StartInfo(paths.FFmpeg,["-nostdin","-v","error","-i",info.Path,
                "-map","0:v:0","-fps_mode","passthrough","-f","rawvideo","-pix_fmt",info.BitDepth>8?"rgb48le":"rgb24","-"])
                };
            bool started=false;Task<string>? errors=null;
            try {
                if(!process.Start())throw new IOException("Cannot start decoder");
                started=true;errors=process.StandardError.ReadToEndAsync();
                int limit=Math.Min(maxFrames??info.Pts.Length,info.Pts.Length);
                for(int i=0;i<limit;i++) {
                    VideoFrame? frame=new(info.FrameBytes,i,(Rational)(info.Pts[i]-info.Pts[0])*info.TimeBase);
                    try {
                        await process.StandardOutput.BaseStream.ReadExactlyAsync(frame.Memory,linked.Token);
                        await channel.Writer.WriteAsync(frame,linked.Token);frame=null;
                    }finally{frame?.Dispose();}
                }
                channel.Writer.TryComplete();
            }catch(Exception ex){channel.Writer.TryComplete(ex);}
            finally {
                if(started){
                    try { if(!process.HasExited)process.Kill(entireProcessTree:true); }catch(InvalidOperationException){}
                    await process.WaitForExitAsync();await errors!;
                }
            }
        }
    }
}

public sealed class EncodingException(string message):IOException(message);
public sealed class VideoEncoder:IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> errors;
    private bool completed;
    public long FramesWritten{get;private set;}
    public VideoEncoder(AppPaths paths,string output,MediaInfo source,Rational fps,EncoderCapability encoder,
        int quality,string? bitrate,bool youtube,Rational duration)
    {
        var videoStart=source.Start;
        var origin=source.Audio.Select(x=>x.Start).Append(videoStart).Min();
        string matrix=source.ColorSpace is "bt2020nc" or "bt2020ncl"?"bt2020":source.ColorSpace??"";
        string filter=$"scale=out_range={(source.ColorRange=="pc"?"pc":"tv")}";
        if(Meaningful(matrix))filter+=":out_color_matrix="+matrix;
        string pixel=source.BitDepth>8?"p010le":"yuv420p";
        // Software encoders commonly accept planar 10-bit, not semi-planar P010.
        if(source.BitDepth>8&&!encoder.Hardware)pixel="yuv420p10le";
        filter+=",format="+pixel;
        var colour=new List<string>();var parameters=new List<string>();
        foreach(var (flag,name,value) in new[]{("-colorspace","colorspace",source.ColorSpace),("-color_range","range",source.ColorRange),
            ("-color_primaries","color_primaries",source.ColorPrimaries),("-color_trc","color_trc",source.ColorTransfer)}) {
            if(!Meaningful(value))continue;
            colour.AddRange([flag,value!]);parameters.Add(name+"="+(name=="range"?(value=="pc"?"full":"limited"):value));
        }
        if(parameters.Count>0)filter+=",setparams="+string.Join(':',parameters);
        var args=new List<string>{"-y","-nostdin","-v","error","-copyts","-f","rawvideo","-pix_fmt",source.BitDepth>8?"rgb48le":"rgb24",
            "-s",$"{source.Width}x{source.Height}","-framerate",fps.ToString(),"-itsoffset",(videoStart-origin).Seconds(),"-i","-",
            "-itsoffset",((Rational)0-origin).Seconds(),"-i",source.Path,"-map","0:v:0","-map","1:a?","-vf",filter};
        args.AddRange(EncoderPolicy.Arguments(encoder,quality,bitrate));args.AddRange(colour);
        var bitstream=ColourBitstream(encoder.Codec,source);
        if(bitstream is not null)args.AddRange(["-bsf:v",bitstream]);
        args.AddRange(["-fps_mode","passthrough","-enc_time_base:v",((Rational)1/fps).ToString()]);
        if(youtube){
            if(fps>60)throw new ArgumentException("YouTube preset supports up to 60fps");
            args.AddRange(["-g",Math.Max(1,(int)Math.Round(fps.ToDouble()/2)).ToString(),"-flags","+cgop",
                "-c:a","aac","-profile:a","aac_low","-ar:a","48000","-af","aresample=async=1:first_pts=0"]);
            if(encoder.Codec=="h264")args.AddRange(["-profile:v","high","-bf","2"]);
            for(int i=0;i<source.Audio.Length;i++){
                int count=source.Audio[i].Channels is 1 or 2 or 6?source.Audio[i].Channels:2;
                args.AddRange([$"-ac:a:{i}",count.ToString(),$"-b:a:{i}",(count==1?128000:count==6?512000:384000).ToString()]);
            }
        }else args.AddRange(["-c:a","copy"]);
        args.AddRange(["-t",(videoStart-origin+duration).Seconds(),"-movflags","+faststart"]);
        if(encoder.Codec=="hevc")args.AddRange(["-tag:v","hvc1"]);
        args.Add(output);
        var start=ProcessRunner.StartInfo(paths.FFmpeg,args);start.RedirectStandardInput=true;
        process=Process.Start(start)??throw new IOException("Cannot start encoder");errors=process.StandardError.ReadToEndAsync();
    }
    private static bool Meaningful(string? value)=>value is not (null or "" or "unknown" or "unspecified" or "N/A");
    private static string? ColourBitstream(string codec,MediaInfo info){
        int? Code(string? value)=>value switch{
            "gbr"=>0,"bt709"=>1,"bt470m"=>4,"bt470bg"=>5,"smpte170m"=>6,"smpte240m"=>7,
            "linear"=>8,"bt2020" or "bt2020nc" or "bt2020ncl"=>9,"bt2020c" or "bt2020cl"=>10,
            "iec61966-2-1"=>13,"bt2020-10"=>14,"bt2020-12"=>15,"smpte2084"=>16,"arib-std-b67"=>18,_=>null};
        var settings=new List<string>();
        if(Code(info.ColorPrimaries) is { } p)settings.Add((codec=="av1"?"color_primaries":"colour_primaries")+"="+p);
        if(Code(info.ColorTransfer) is { } t)settings.Add("transfer_characteristics="+t);
        if(Code(info.ColorSpace) is { } m)settings.Add("matrix_coefficients="+m);
        if(info.ColorRange is "pc" or "tv")settings.Add((codec=="av1"?"color_range":"video_full_range_flag")+"="+(info.ColorRange=="pc"?1:0));
        return settings.Count==0?null:codec+"_metadata="+string.Join(':',settings);
    }
    public async Task WriteAsync(ReadOnlyMemory<byte> frame,CancellationToken cancel){
        try{await process.StandardInput.BaseStream.WriteAsync(frame,cancel);FramesWritten++;}
        catch(IOException){await process.WaitForExitAsync(cancel);throw new EncodingException("Encoding failed: "+await errors);}
    }
    public async Task CompleteAsync(CancellationToken cancel){
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancel);
        if(process.ExitCode!=0)throw new EncodingException("Encoding failed: "+await errors);
        completed=true;
    }
    public async ValueTask DisposeAsync(){
        if(!completed){try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}}
        await process.WaitForExitAsync();await errors;process.Dispose();
    }
}
