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
        string? oldWorkers=Environment.GetEnvironmentVariable("FRAMEDOUBLER_ENGINE_WORKERS");
        try{
            Environment.SetEnvironmentVariable("FRAMEDOUBLER_ENGINE_WORKERS","1");
            var single=await pipeline.RunAsync(source,Path.Combine(root,"single60.mp4"),options);
            async Task<string> FramesHash(string file){var r=await ProcessRunner.RunAsync(paths.FFmpeg,["-v","error","-i",file,"-map","0:v:0","-f","framemd5","-"]);if(r.ExitCode!=0)throw new IOException(r.Error);return r.Output;}
            assert(normal.Interpolated==single.Interpolated&&normal.Frames==single.Frames&&await FramesHash(normal.Output)==await FramesHash(single.Output),"Ordered parallel pipeline matches every decoded serial output frame");
        }finally{Environment.SetEnvironmentVariable("FRAMEDOUBLER_ENGINE_WORKERS",oldWorkers);}
        // Match source: same codec as the input (H.264 here), bitrate from the source, audio copied.
        var matched=await pipeline.RunAsync(source,Path.Combine(root,"match-source.mp4"),options with{MatchSource=true});
        var matchedInfo=await MediaProbe.ReadAsync(paths,matched.Output);
        assert(matchedInfo.Codec==info.Codec&&matchedInfo.Width==info.Width&&matched.Verification?.Ok==true&&matched.Fps==60,"Match-source preset keeps codec and size");
        async Task<string> AudioHash(string path){var result=await ProcessRunner.RunAsync(paths.FFmpeg,["-v","error","-i",path,"-map","0:a:0","-c:a","copy","-f","hash","-hash","sha256","-"]);if(result.ExitCode!=0)throw new IOException(result.Error);return result.Output.Trim();}
        assert(await AudioHash(source)==await AudioHash(matched.Output),"Source preset audio packets are passed through unchanged");
        // Requested output height (CLI --height 90) with verification; the audio is still copied unchanged.
        var scaled=await pipeline.RunAsync(source,Path.Combine(root,"scaled90.mp4"),options with{OutputHeight=90});
        var scaledInfo=await MediaProbe.ReadAsync(paths,scaled.Output);
        assert(scaledInfo.Width==160&&scaledInfo.Height==90&&scaled.Verification?.Ok==true&&await AudioHash(source)==await AudioHash(scaled.Output),"Requested height scales the output and keeps the audio packets");
        assert(normal.Frames==120&&normal.Interpolated==59&&normal.Verification?.Ok==true,"True 30 to 60 inference and audio verification");
        // Portrait phone recording: a quarter-turn display matrix makes FFmpeg decode upright 180×320
        // frames. Reading them as 320×180 used to shear the picture into three striped copies.
        string landscape=Path.Combine(root,"landscape.mp4"),rotated=Path.Combine(root,"portrait-rotated.mp4");
        await FF("-f","lavfi","-i","testsrc2=size=320x180:rate=30:duration=1","-c:v","libopenh264","-b:v","2M",landscape);
        await FF("-display_rotation","90","-i",landscape,"-c","copy",rotated);
        var rotatedInfo=await MediaProbe.ReadAsync(paths,rotated);
        assert(rotatedInfo.Width==180&&rotatedInfo.Height==320&&rotatedInfo.Rotation==270,"Rotated input reports the upright picture size");
        var upright=await pipeline.RunAsync(rotated,Path.Combine(root,"portrait60.mp4"),options);
        var uprightInfo=await MediaProbe.ReadAsync(paths,upright.Output);
        var psnr=await ProcessRunner.RunAsync(paths.FFmpeg,["-hide_banner","-v","info","-nostdin","-i",upright.Output,"-i",rotated,"-filter_complex",
            "[0:v]trim=end_frame=1[a];[1:v]trim=end_frame=1[b];[a][b]psnr","-f","null","-"]);
        var average=System.Text.RegularExpressions.Regex.Match(psnr.Error,@"average:(\S+)");
        double firstFrameDb=average.Success?average.Groups[1].Value=="inf"?99:double.Parse(average.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture):0;
        assert(psnr.ExitCode==0&&firstFrameDb>30&&uprightInfo.Width==180&&uprightInfo.Height==320&&uprightInfo.Rotation==0&&upright.Frames==60&&upright.Verification?.Ok==true,
            $"Portrait output is upright without a second rotation tag (first frame {firstFrameDb:0.0}dB)");
        // Anamorphic portrait: 320×180 stored with 81:256 pixels is shown as 9:16. The raw RGB pipe dropped the
        // pixel shape, so the output played as a stretched 16:9 picture.
        async Task<double> LumaPsnr(string a,string b,string referenceFilter=""){
            var r=await ProcessRunner.RunAsync(paths.FFmpeg,["-hide_banner","-v","info","-nostdin","-i",a,"-i",b,"-filter_complex",
                $"[0:v]trim=end_frame=1,format=gray[x];[1:v]trim=end_frame=1{referenceFilter},format=gray[y];[x][y]psnr","-f","null","-"]);
            var m=System.Text.RegularExpressions.Regex.Match(r.Error,@"average:(\S+)");
            return r.ExitCode!=0||!m.Success?0:m.Groups[1].Value=="inf"?99:double.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
        }
        string anamorphic=Path.Combine(root,"anamorphic.mp4"),anamorphicRotated=Path.Combine(root,"anamorphic-rotated.mp4");
        await FF("-f","lavfi","-i","testsrc2=size=320x180:rate=30:duration=1","-vf","setsar=sar=81/256:max=256","-c:v","libopenh264","-b:v","2M",anamorphic);
        var anamorphicInfo=await MediaProbe.ReadAsync(paths,anamorphic);
        assert(anamorphicInfo is {Width:320,Height:180,Anamorphic:true}&&anamorphicInfo.SampleAspect==new Rational(81,256)&&Math.Abs(anamorphicInfo.DisplayAspect-9/16.0)<1e-9,
            "Anamorphic input keeps the stored size and reports its 81:256 pixels");
        var kept=await pipeline.RunAsync(anamorphic,Path.Combine(root,"anamorphic60.mp4"),options);
        var keptInfo=await MediaProbe.ReadAsync(paths,kept.Output);
        double keptDb=await LumaPsnr(kept.Output,anamorphic);
        assert(keptInfo is {Width:320,Height:180}&&keptInfo.SampleAspect==new Rational(81,256)&&kept.Frames==60&&kept.Verification?.Ok==true&&keptDb>30,
            $"Anamorphic output keeps the stored pixels and their 81:256 shape ({keptDb:0.0}dB)");
        var squared=await pipeline.RunAsync(anamorphic,Path.Combine(root,"anamorphic-square.mp4"),options with{OutputHeight=180});
        var squaredInfo=await MediaProbe.ReadAsync(paths,squared.Output);
        double squaredDb=await LumaPsnr(squared.Output,anamorphic,",scale=180:320:flags=lanczos");
        assert(squaredInfo is {Width:180,Height:320,Anamorphic:false}&&squared.Verification?.Ok==true&&squaredDb>30,
            $"Preset resize turns anamorphic pixels square at the displayed 9:16 shape ({squaredDb:0.0}dB)");
        await FF("-display_rotation","90","-i",anamorphic,"-c","copy",anamorphicRotated);
        var anamorphicRotatedInfo=await MediaProbe.ReadAsync(paths,anamorphicRotated);
        var turned=await pipeline.RunAsync(anamorphicRotated,Path.Combine(root,"anamorphic-rotated60.mp4"),options);
        var turnedInfo=await MediaProbe.ReadAsync(paths,turned.Output);
        double turnedDb=await LumaPsnr(turned.Output,anamorphicRotated);
        assert(anamorphicRotatedInfo is {Width:180,Height:320,Rotation:270}&&anamorphicRotatedInfo.SampleAspect==new Rational(256,81)
            &&turnedInfo is {Width:180,Height:320,Rotation:0}&&turnedInfo.SampleAspect==new Rational(256,81)&&turned.Verification?.Ok==true&&turnedDb>30,
            $"A rotated anamorphic input turns its pixel shape with the picture ({turnedDb:0.0}dB)");
        var anamorphicThumb=await Thumbnail.CaptureAsync(paths,anamorphicInfo,128);
        int anamorphicThumbWidth=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(anamorphicThumb.AsSpan(16)),anamorphicThumbHeight=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(anamorphicThumb.AsSpan(20));
        assert(anamorphicThumbWidth==128&&anamorphicThumbHeight==228,$"Anamorphic thumbnail has the displayed 9:16 shape ({anamorphicThumbWidth}x{anamorphicThumbHeight})");
        // Losslessly trimmed clip: the MP4 edit list starts after the key frame, so the packets before the cut
        // are flagged discard and never output. Counting them made the decoder wait for frames that never came.
        string keyed=Path.Combine(root,"gop30.mp4"),trimmed=Path.Combine(root,"trimmed.mp4");
        await FF("-f","lavfi","-i","testsrc2=size=320x180:rate=30:duration=2","-f","lavfi","-i","sine=frequency=500:sample_rate=48000:duration=2",
            "-c:v","libopenh264","-b:v","2M","-g","30","-c:a","aac","-shortest",keyed);
        await FF("-ss","0.5","-i",keyed,"-t","1","-c","copy",trimmed);
        var trimmedInfo=await MediaProbe.ReadAsync(paths,trimmed);
        var trimmedResult=await pipeline.RunAsync(trimmed,Path.Combine(root,"trimmed60.mp4"),options);
        assert(trimmedInfo.Pts.Length==30&&trimmedResult.Frames==60&&trimmedResult.Verification?.Ok==true,
            $"Losslessly trimmed clip skips edit-list discard packets ({trimmedInfo.Pts.Length} frames, {trimmedResult.Frames} out, {string.Join("; ",trimmedResult.Verification?.Problems??[])})");
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
        string cuts=Path.Combine(root,"cuts-static-motion.mp4");
        await FF("-f","lavfi","-i","color=black:s=320x180:r=30:d=0.2","-f","lavfi","-i","color=white:s=320x180:r=30:d=0.2",
            "-f","lavfi","-i","testsrc2=size=320x180:rate=30:duration=0.8","-filter_complex","[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]","-map","[v]","-c:v","libopenh264","-b:v","2M",cuts);
        var cutResult=await pipeline.RunAsync(cuts,Path.Combine(root,"cuts-static-motion60.mp4"),options);
        assert(cutResult.SceneCuts>=1&&cutResult.Verification is {Ok:true,ExpectedContentHolds:>0,DuplicateRatio:>.01},"Source-proven cut/static holds are reported separately without false quality failure");
        string repeatedCuts=Path.Combine(root,"cuts-repeated60.mp4");
        await FF("-i",cuts,"-vf","fps=60","-c:v","libopenh264","-b:v","2M",repeatedCuts);
        assert(!(await Verifier.VerifyAsync(paths,cuts,repeatedCuts,60)).Ok,"A clip with genuine cuts still rejects repeated moving frames");
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
        string pq=Path.Combine(root,"hdr-tag.mp4");
        await FF("-f","lavfi","-i","color=s=320x184:r=30:d=0.1,format=yuv420p,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc",
            "-frames:v","3","-an","-c:v","libkvazaar","-kvazaar-params","threads=2",
            "-color_trc","smpte2084","-color_primaries","bt2020","-colorspace","bt2020nc",
            "-bsf:v","hevc_metadata=colour_primaries=9:transfer_characteristics=16:matrix_coefficients=9",pq);
        var pqInfo=await MediaProbe.ReadAsync(paths,pq);
        assert(pqInfo.HdrInput?.Name=="PQ"&&pqInfo.BitDepth==8&&pqInfo.ColorTransfer=="bt709"&&pqInfo.ColorPrimaries=="bt709","PQ input is processed as tone-mapped BT.709");
        // HLG 10-bit AV1: converted to verified 8-bit BT.709 SDR.
        string hlg=Path.Combine(root,"hlg.mp4");
        await FF("-f","lavfi","-i","testsrc2=size=320x180:rate=30:duration=1,format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc:range=tv",
            "-f","lavfi","-i","sine=frequency=500:sample_rate=48000:duration=1","-c:v","libsvtav1","-preset","10","-svtav1-params","lp=2","-pix_fmt","yuv420p10le",
            "-color_trc","arib-std-b67","-color_primaries","bt2020","-colorspace","bt2020nc","-c:a","aac","-shortest",
            "-bsf:v","av1_metadata=color_primaries=9:transfer_characteristics=18:matrix_coefficients=9:color_range=0",hlg);
        var hlgInfo=await MediaProbe.ReadAsync(paths,hlg);
        assert(hlgInfo.HdrInput is {Name:"HLG",BitDepth:10}&&hlgInfo.BitDepth==8&&hlgInfo.FrameBytes==320*180*3,"HLG 10-bit input decodes as 8-bit SDR frames");
        var hlgResult=await pipeline.RunAsync(hlg,Path.Combine(root,"hlg60.mp4"),options);
        var hlgOut=await MediaProbe.ReadAsync(paths,hlgResult.Output);
        assert(hlgOut.HdrInput is null&&hlgOut.BitDepth==8&&hlgOut.ColorTransfer=="bt709"&&hlgOut.ColorPrimaries=="bt709"&&hlgOut.ColorSpace=="bt709"&&hlgOut.ColorRange=="tv"
            &&hlgResult.Fps==60&&hlgResult.Verification?.Ok==true,"HLG converts to verified SDR BT.709 60fps");
        // Portrait phone HDR (HLG with a quarter-turn display matrix): rotation and tone mapping together.
        string hlgPortrait=Path.Combine(root,"hlg-portrait.mp4");
        await FF("-display_rotation","90","-i",hlg,"-c","copy",hlgPortrait);
        var hlgPortraitInfo=await MediaProbe.ReadAsync(paths,hlgPortrait);
        assert(hlgPortraitInfo is {Width:180,Height:320,Rotation:270,BitDepth:8,HdrInput.Name:"HLG"},"Rotated HLG input reports the upright size and is processed as SDR");
        var hlgPortraitResult=await pipeline.RunAsync(hlgPortrait,Path.Combine(root,"hlg-portrait60.mp4"),options);
        var hlgPortraitOut=await MediaProbe.ReadAsync(paths,hlgPortraitResult.Output);
        string tonemapped=Path.Combine(root,"hlg-portrait-sdr.png"),untouched=Path.Combine(root,"hlg-portrait-raw.png");
        await FF("-i",hlgPortrait,"-vf",ToneMap.Filter(hlgPortraitInfo.HdrInput!),"-frames:v","1",tonemapped);
        await FF("-i",hlgPortrait,"-frames:v","1",untouched);
        async Task<double> FirstFramePsnr(string a,string b){
            // Luma only: the encoded output is 4:2:0, the PNG references are full-colour RGB.
            var r=await ProcessRunner.RunAsync(paths.FFmpeg,["-hide_banner","-v","info","-nostdin","-i",a,"-i",b,"-filter_complex","[0:v]trim=end_frame=1,format=rgb24,format=gray[x];[1:v]trim=end_frame=1,format=rgb24,format=gray[y];[x][y]psnr","-f","null","-"]);
            var m=System.Text.RegularExpressions.Regex.Match(r.Error,@"average:(\S+)");
            return r.ExitCode!=0||!m.Success?0:m.Groups[1].Value=="inf"?99:double.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
        }
        double toTonemapped=await FirstFramePsnr(hlgPortraitResult.Output,tonemapped),toUntouched=await FirstFramePsnr(hlgPortraitResult.Output,untouched);
        assert(hlgPortraitOut is {Width:180,Height:320,Rotation:0,BitDepth:8,ColorTransfer:"bt709",ColorPrimaries:"bt709",HdrInput:null}&&hlgPortraitResult.Verification?.Ok==true
            &&toTonemapped>35&&toTonemapped>toUntouched+10,
            $"Rotated HLG becomes upright tone-mapped SDR (first frame {toTonemapped:0.0}dB to the tone-mapped reference, {toUntouched:0.0}dB to the untouched picture)");
        var hlgThumb=await Thumbnail.CaptureAsync(paths,hlgPortraitInfo,128);
        int thumbWidth=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(hlgThumb.AsSpan(16)),thumbHeight=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(hlgThumb.AsSpan(20));
        assert(thumbWidth==128&&thumbHeight>thumbWidth,$"Rotated HDR thumbnail is upright ({thumbWidth}x{thumbHeight})");
        // A folder name ending in Hangul followed by '(' broke ffprobe JSON decoded with the ANSI code page.
        string hangulFolder=Path.Combine(root,"한글 시리즈");Directory.CreateDirectory(hangulFolder);
        string hangulInput=Path.Combine(hangulFolder,"(시험) 입력.mkv");await FF("-i",source,"-c","copy",hangulInput);
        var hangul=await pipeline.AnalyzeAsync(hangulInput);
        assert(hangul.Source.Path==hangulInput&&hangul.SampledFrames>0,"Hangul folder + '(' file name analyses");
        string delayed=Path.Combine(root,"late-audio.mp4");
        await FF("-f","lavfi","-i","testsrc2=size=320x180:rate=30:duration=2.4","-itsoffset","0.25","-f","lavfi","-i","sine=duration=2:sample_rate=48000",
            "-c:v","libopenh264","-b:v","2M","-c:a","aac","-shortest",delayed);
        var sync=await pipeline.RunAsync(delayed,Path.Combine(root,"late-audio60.mp4"),options);
        assert(sync.Verification?.Ok==true,"Delayed audio stays synchronized when copied");
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
