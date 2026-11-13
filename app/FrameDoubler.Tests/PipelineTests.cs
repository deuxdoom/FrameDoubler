using FrameDoubler.Core;
using System.Text.Json;

internal static class PipelineTests
{
    private sealed class InlineProgress(Action<ConversionProgress> callback):IProgress<ConversionProgress>{public void Report(ConversionProgress value)=>callback(value);}
    public static async Task RunAsync(AppPaths paths,Action<bool,string> assert)
    {
        string root=Path.Combine(EncoderDetector.WorkRoot,"pipeline-tests",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        async Task FF(params string[] args){var r=await ProcessRunner.RunAsync(paths.FFmpeg,new[]{"-v","error","-y"}.Concat(args),timeout:TimeSpan.FromMinutes(2));if(r.ExitCode!=0)throw new IOException(r.Error);}
        string source=Path.Combine(root,"30.mp4");
        await FF("-f","lavfi","-i","testsrc2=size=320x180:rate=30:duration=2","-f","lavfi","-i","sine=frequency=500:sample_rate=48000:duration=2",
            "-c:v","libopenh264","-b:v","2M","-c:a","aac","-colorspace","bt709","-color_primaries","bt709","-color_trc","bt709","-color_range","tv",
            "-bsf:v","h264_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1:video_full_range_flag=0","-shortest",source);
        var pipeline=new ConversionPipeline(paths);
        var info=await MediaProbe.ReadAsync(paths,source);
        assert(info.Fps==30&&info.Pts.Length==60,"Probe 30fps timestamps");
        var options=new ConversionOptions{Codec="h264",Verify=true};
        var normal=await pipeline.RunAsync(source,Path.Combine(root,"60.mp4"),options);
        assert(normal.Frames==120&&normal.Interpolated==59&&normal.Verification?.Ok==true,"True 30 to 60 inference and audio verification");
        string small=Path.Combine(root,"small.mp4");
        await FF("-f","lavfi","-i","color=s=128x128:r=30:d=0.4","-c:v","libopenh264","-b:v","1M",small);
        var fallback=await pipeline.RunAsync(small,Path.Combine(root,"small60.mp4"),options);
        assert(fallback.Frames==24&&fallback.Verification?.Ok==true,"Encoder dimension failure falls back automatically or succeeds on available hardware");
        string duplicate=Path.Combine(root,"duplicate60.mp4");
        await FF("-i",source,"-vf","fps=60","-c:v","libopenh264","-b:v","2M","-c:a","copy",duplicate);
        var restored=await pipeline.RunAsync(duplicate,Path.Combine(root,"restored60.mp4"),options);
        assert(restored.Frames==120&&restored.Duplicates>=50&&restored.Interpolated>=50&&restored.Verification?.Ok==true,"Restore 30-motion capture at 60fps");
        var rejectedRepeat=await Verifier.VerifyAsync(paths,source,duplicate,60);
        assert(!rejectedRepeat.Ok,"Frame doubling without AI must fail moving-duplicate verification");
        string repeat120=Path.Combine(root,"repeat120.mp4");
        await FF("-i",source,"-vf","fps=120","-c:v","libopenh264","-b:v","2M","-c:a","copy",repeat120);
        assert(!(await Verifier.VerifyAsync(paths,source,repeat120,120)).Ok,"120fps repeated frames must still fail verification");
        var high=await pipeline.RunAsync(source,Path.Combine(root,"120.mp4"),options with{Fps=120,Model="v4.26"});
        assert(high.Frames==240&&high.Verification?.Fps==120&&high.Verification.Problems.All(x=>x.Contains("moving frames remain duplicates",StringComparison.Ordinal)),
            "120fps exact timeline; preserve and expose any heuristic quality warning");
        string ntsc=Path.Combine(root,"ntsc.mp4");
        await FF("-f","lavfi","-i","testsrc2=size=320x180:rate=30000/1001:duration=2","-c:v","libopenh264","-b:v","2M",ntsc);
        var fractional=await pipeline.RunAsync(ntsc,Path.Combine(root,"ntsc60.mp4"),options);
        assert(fractional.Fps==Rational.Parse("60000/1001")&&fractional.Frames==120&&fractional.Verification?.Ok==true,"29.97 to exact 59.94");
        string variable=Path.Combine(root,"vfr.mp4");
        await FF("-i",source,"-vf","select='not(eq(mod(n,11),4))'","-fps_mode","vfr","-c:v","libopenh264","-b:v","2M","-c:a","copy",variable);
        var variableInfo=await MediaProbe.ReadAsync(paths,variable);
        var variableResult=await pipeline.RunAsync(variable,Path.Combine(root,"vfr60.mp4"),options);
        assert(variableInfo.IsVfr&&variableResult.Fps==60&&Math.Abs(variableResult.Verification!.DurationDifference)<1d/60,"VFR/drop timeline and duration");
        string deep=Path.Combine(root,"10bit.mp4");
        await FF("-i",source,"-vf","format=yuv420p10le","-c:v","libsvtav1","-preset","10","-svtav1-params","lp=2","-pix_fmt","yuv420p10le","-c:a","copy",
            "-bsf:v","av1_metadata=color_primaries=1:transfer_characteristics=1:matrix_coefficients=1:color_range=0",deep);
        var deepResult=await pipeline.RunAsync(deep,Path.Combine(root,"10bit60.mp4"),options with{Codec="av1",HardwareEncoding=false,Device="cpu"});
        var deepInfo=await MediaProbe.ReadAsync(paths,deepResult.Output);
        assert(deepInfo.BitDepth==10&&deepResult.Verification?.Ok==true,"10-bit SDR RGB16 CPU inference and software AV1 output");
        string hdr=Path.Combine(root,"hdr-tag.mp4");
        await FF("-f","lavfi","-i","color=s=320x184:r=30:d=0.1,format=yuv420p,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc",
            "-frames:v","3","-an","-c:v","libkvazaar","-kvazaar-params","threads=2",
            "-color_trc","smpte2084","-color_primaries","bt2020","-colorspace","bt2020nc",
            "-bsf:v","hevc_metadata=colour_primaries=9:transfer_characteristics=16:matrix_coefficients=9",hdr);
        bool hdrRejected=false;try{await pipeline.RunAsync(hdr,Path.Combine(root,"unsupported-hdr.mp4"),options);}catch(NotSupportedException){hdrRejected=true;}
        assert(hdrRejected,"HDR rejection before conversion");
        string delayed=Path.Combine(root,"late-audio.mp4");
        await FF("-f","lavfi","-i","testsrc2=size=320x180:rate=30:duration=2.4","-itsoffset","0.25","-f","lavfi","-i","sine=duration=2:sample_rate=48000",
            "-c:v","libopenh264","-b:v","2M","-c:a","aac","-shortest",delayed);
        var sync=await pipeline.RunAsync(delayed,Path.Combine(root,"late-audio60.mp4"),options with{Youtube=true});
        assert(sync.Verification?.Ok==true,"Delayed audio and AAC synchronization");
        string? old=Environment.GetEnvironmentVariable("FRAMEDOUBLER_DISABLE_VULKAN");
        try{
            Environment.SetEnvironmentVariable("FRAMEDOUBLER_DISABLE_VULKAN","1");
            var cpu=await new ConversionPipeline(paths).RunAsync(source,Path.Combine(root,"fallback60.mp4"),options with{HardwareEncoding=false});
            assert(cpu.Device=="cpu"&&cpu.Encoder=="libopenh264"&&cpu.Verification?.Ok==true,"No Vulkan plus software encoding end to end");
        }finally{Environment.SetEnvironmentVariable("FRAMEDOUBLER_DISABLE_VULKAN",old);}
        string protectedFile=Path.Combine(root,"protected.mp4");await File.WriteAllTextAsync(protectedFile,"KEEP");
        bool rejected=false;try{await pipeline.RunAsync(source,protectedFile,options);}catch(IOException){rejected=true;}
        assert(rejected&&await File.ReadAllTextAsync(protectedFile)=="KEEP","Protect existing output");
        using var stop=new CancellationTokenSource();bool cancelled=false;string cancelledFile=Path.Combine(root,"cancelled.mp4");
        try{
            await pipeline.RunAsync(source,cancelledFile,options,new InlineProgress(p=>{if(p.Stage=="convert")stop.Cancel();}),stop.Token);
        }catch(OperationCanceledException){cancelled=true;}
        assert(cancelled&&!File.Exists(cancelledFile)&&Directory.GetFiles(root,"*.partial.mp4").Length==0,"Mid-conversion cancellation and cleanup");
        string unicode=Path.Combine(root,"한글 입력.mp4");File.Copy(source,unicode);
        var named=await pipeline.RunAsync(unicode,Path.Combine(root,"한글 출력.mp4"),options);
        assert(named.Verification?.Ok==true,"Unicode input and output");
        var inventory=await HardwareDetector.DetectAsync(paths,false);
        await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{normal,restored,high,fractional,deepResult,sync,named,inventory},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Pipeline artifacts: "+root);
    }
    public static async Task LongAsync(AppPaths paths,Action<bool,string> assert)
    {
        string root=Path.Combine(EncoderDetector.WorkRoot,"long-sync");Directory.CreateDirectory(root);
        string source=Path.Combine(root,"ntsc600.mp4");
        if(!File.Exists(source)){
            var run=await ProcessRunner.RunAsync(paths.FFmpeg,["-v","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=60000/1001:duration=600.6",
                "-f","lavfi","-i","sine=sample_rate=48000:duration=600.6","-c:v","libopenh264","-b:v","2M","-c:a","aac","-shortest",source],timeout:TimeSpan.FromMinutes(5));
            if(run.ExitCode!=0)throw new IOException(run.Error);
        }
        var result=await new ConversionPipeline(paths).RunAsync(source,Path.Combine(root,"ntsc600-out.mp4"),
            new(){Fps=Rational.Parse("60000/1001"),Codec="h264",Deduplicate=false,Overwrite=true});
        assert(result.Frames==36000&&result.Verification?.Ok==true,"600.6-second exact NTSC timing and audio synchronization");
        await File.WriteAllTextAsync(Path.Combine(root,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
    }
}
