using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FrameDoubler.Core;

public static class Decoder
{
    // Bounded, seeked preflight only. Conversion always uses the exact packet PTS path below.
    public static async IAsyncEnumerable<VideoFrame> WindowAsync(AppPaths paths,MediaInfo info,double offset,int count,
        [EnumeratorCancellation]CancellationToken cancel=default){
        string[] args=["-nostdin","-v","error","-ss",offset.ToString("0.######",System.Globalization.CultureInfo.InvariantCulture),"-i",info.Path,
            "-map","0:v:0",..ToneMap.Arguments(info),"-frames:v",count.ToString(),"-fps_mode","passthrough","-f","rawvideo","-pix_fmt",info.BitDepth>8?"rgb48le":"rgb24","-"];
        using var process=ProcessRunner.Start(ProcessRunner.StartInfo(paths.FFmpeg,args),"sample decoder");
        var errors=process.StandardError.ReadToEndAsync();
        using var kill=cancel.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){} });
        try{for(int i=0;i<count;i++){
            VideoFrame? frame=new(info.FrameBytes,i,(Rational)i/info.Fps);
            try{int first=await process.StandardOutput.BaseStream.ReadAsync(frame.Memory[..1],cancel);if(first==0){cancel.ThrowIfCancellationRequested();yield break;}
                await process.StandardOutput.BaseStream.ReadExactlyAsync(frame.Memory[1..],cancel);
                var result=frame;frame=null;yield return result;
            }finally{frame?.Dispose();}
        }}finally{
            try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}
            await process.WaitForExitAsync();await errors;
        }
    }
    public static async IAsyncEnumerable<VideoFrame> FramesAsync(AppPaths paths,MediaInfo info,int? maxFrames=null,
        [EnumeratorCancellation] CancellationToken cancel=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var channel=Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(2){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
        var produce=ProduceAsync();
        try { await foreach(var frame in channel.Reader.ReadAllAsync(cancel))yield return frame; }
        finally { linked.Cancel();try{await produce;}catch(OperationCanceledException){}finally{while(channel.Reader.TryRead(out var extra))extra.Dispose();} }

        async Task ProduceAsync() {
            Process? process=null;
            bool started=false;Task<string>? errors=null;
            try {
                process=ProcessRunner.Start(ProcessRunner.StartInfo(paths.FFmpeg,["-nostdin","-v","error","-i",info.Path,
                    "-map","0:v:0",..ToneMap.Arguments(info),"-fps_mode","passthrough","-f","rawvideo","-pix_fmt",info.BitDepth>8?"rgb48le":"rgb24","-"]),"decoder");
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
                    try { if(!process!.HasExited)process.Kill(entireProcessTree:true); }catch(InvalidOperationException){}
                    await process!.WaitForExitAsync();await errors!;
                }
                process?.Dispose();
            }
        }
    }
}

// HDR (PQ/HLG) input is tone-mapped once, at decode time, to 8-bit BT.709 SDR RGB. Analysis,
// time estimates, interpolation, encoding and verification therefore all see the same SDR frames.
// Hable with desaturation off kept highlights and skin closest to the libplacebo BT.2390
// reference on a real 4K HLG stage recording while staying on the CPU.
public static class ToneMap
{
    public static string[] Arguments(MediaInfo info)=>info.HdrInput is { } hdr?["-vf",Filter(hdr)]:[];
    public static string Filter(HdrSource hdr){
        static bool Known(string? value)=>value is not (null or "" or "unknown" or "unspecified" or "reserved");
        var input=new List<string>{"tin="+hdr.Transfer,"pin="+(Known(hdr.Primaries)?hdr.Primaries:"bt2020"),
            "min="+(Known(hdr.Matrix)?hdr.Matrix:"bt2020nc"),"rin="+(hdr.Range=="pc"?"pc":"tv")};
        return $"zscale={string.Join(':',input)}:t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709,format=gbrp";
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
        int quality,string? bitrate,string? platform,Rational duration,int? height=null)
    {
        var videoStart=source.Start;
        var origin=source.Audio.Select(x=>x.Start).Append(videoStart).Min();
        string matrix=source.ColorSpace is "bt2020nc" or "bt2020ncl"?"bt2020":source.ColorSpace??"";
        // Optional resize to the preset height (Lanczos), done in the same scale step as the RGB→YUV conversion.
        var (outWidth,outHeight)=OutputPresets.OutputSize(source.Width,source.Height,height,source.SampleAspect);
        bool resized=outWidth!=source.Width||outHeight!=source.Height;
        string resize=resized?$"w={outWidth}:h={outHeight}:flags=lanczos:":"";
        string filter=$"scale={resize}out_range={(source.ColorRange=="pc"?"pc":"tv")}";
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
        // Raw RGB on the pipe carries no pixel shape, so anamorphic output would play stretched to the
        // stored size. Keep the source ratio, or square pixels after a preset resize to the displayed shape.
        // setsar rounds to terms up to "max" (100 by default), which would distort ratios such as 81:256.
        if(source.Anamorphic){
            var sar=resized?(Rational)1:source.SampleAspect;
            filter+=$",setsar=sar={sar.Numerator}/{sar.Denominator}:max={System.Numerics.BigInteger.Max(sar.Numerator,sar.Denominator)}";
        }
        var args=new List<string>{"-y","-nostdin","-v","error","-copyts","-f","rawvideo","-pix_fmt",source.BitDepth>8?"rgb48le":"rgb24",
            "-s",$"{source.Width}x{source.Height}","-framerate",fps.ToString(),"-itsoffset",(videoStart-origin).Seconds(),"-i","-",
            "-itsoffset",((Rational)0-origin).Seconds(),"-i",source.Path,"-map","0:v:0","-map","1:a?","-vf",filter};
        args.AddRange(EncoderPolicy.Arguments(encoder,quality,bitrate));args.AddRange(colour);
        var bitstream=ColourBitstream(encoder.Codec,source);
        if(bitstream is not null)args.AddRange(["-bsf:v",bitstream]);
        args.AddRange(["-fps_mode","passthrough","-enc_time_base:v",((Rational)1/fps).ToString()]);
        if(platform is not null){
            if(fps>60)throw new ArgumentException("선택한 플랫폼 프리셋은 60fps까지 지원합니다.");
            args.AddRange(["-g",Math.Max(1,(int)Math.Round(fps.ToDouble()*OutputPresets.GopSeconds(platform))).ToString(),"-flags","+cgop",
                "-c:a","aac","-profile:a","aac_low","-ar:a","48000","-af","aresample=async=1:first_pts=0"]);
            if(encoder.Codec=="h264")args.AddRange(["-profile:v","high","-bf","2"]);
            for(int i=0;i<source.Audio.Length;i++){
                // App audio policy: 256k, or 192k for source tracks below 256k/unknown.
                if(platform=="youtube"){
                    int count=source.Audio[i].Channels is 1 or 2 or 6?source.Audio[i].Channels:2;
                    args.AddRange([$"-ac:a:{i}",count.ToString(),$"-b:a:{i}",OutputPresets.AudioBitrate(source.Audio[i]).ToString()]);
                }else args.AddRange([$"-ac:a:{i}","2",$"-b:a:{i}","160000"]);
            }
        }else args.AddRange(["-c:a","copy"]);
        args.AddRange(["-t",(videoStart-origin+duration).Seconds(),"-movflags","+faststart"]);
        if(encoder.Codec=="hevc")args.AddRange(["-tag:v","hvc1"]);
        args.Add(output);
        var start=ProcessRunner.StartInfo(paths.FFmpeg,args);start.RedirectStandardInput=true;
        process=ProcessRunner.Start(start,"encoder");errors=process.StandardError.ReadToEndAsync();
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
