using System.Diagnostics;
using System.Security.Cryptography;

namespace FrameDoubler.Core;

public sealed partial class ConversionPipeline
{
    private string? engineFingerprint;
    private async Task<AnalysisResult> SampleAnalysisAsync(MediaInfo info,CancellationToken cancel){
        double duration=info.Duration.ToDouble();
        double[] offsets=duration<2?[0]:new[]{.1,.5,.85}.Select(x=>Math.Max(0,Math.Min(duration-.6,duration*x))).Distinct().ToArray();
        int unique=0,duplicates=0,still=0,count=0,bestUnique=-1;double bestOffset=0,decodeSeconds=0;int measured=0;
        var intervals=new List<SampleInterval>();
        foreach(double offset in offsets){
            var detector=new CadenceDetector();Signature? pending=null;Rational pendingTime=0,lastTime=0;float[]? lastScene=null;
            int windowUnique=0,frames=0;var clock=Stopwatch.StartNew();double warm=0;bool anchor=true;
            void Count(FrameLabel? label){
                if(label is null)return;
                // The first frame of a seeked window has no predecessor and is always labelled new.
                // It anchors the first interval but is not counted, otherwise short windows overstate motion.
                if(anchor){anchor=false;lastTime=pendingTime;lastScene=pending!.Descriptor();return;}
                if(label==FrameLabel.Duplicate){duplicates++;return;}
                if(label==FrameLabel.Static)still++;else{unique++;windowUnique++;}
                var scene=pending!.Descriptor();
                if(lastScene is not null)intervals.Add(new(lastTime,pendingTime,label==FrameLabel.Static||Signature.CutScore(lastScene,scene)>.35f));
                lastTime=pendingTime;lastScene=scene;
            }
            await foreach(var frame in Decoder.WindowAsync(paths,info,offset,16,cancel)){using(frame){
                var signature=Signature.Create(frame,info);Count(detector.Push(signature));pending=signature;pendingTime=frame.Time;frames++;count++;
                if(frames==2)warm=clock.Elapsed.TotalSeconds;
            }}
            Count(detector.Flush());
            if(frames>2){decodeSeconds+=Math.Max(.00001,clock.Elapsed.TotalSeconds-warm);measured+=frames-2;}
            if(windowUnique>bestUnique){bestUnique=windowUnique;bestOffset=offset;}
        }
        if(count==0)throw new IOException("No frames in video samples");
        double? motion=unique<=1||still>count*.8?null:info.Fps.ToDouble()*unique/Math.Max(1,unique+duplicates);
        return new(info,unique,duplicates,still,motion){SampledFrames=count,DecodeSecondsPerFrame=decodeSeconds/Math.Max(1,measured),BestSampleOffset=bestOffset,Intervals=intervals.ToArray()};
    }
    private string PerformanceKey(MediaInfo source,ConversionOptions options,DeviceChoice device,EncoderCapability encoder,Rational fps,int workers){
        engineFingerprint??=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths.Engine)));
        var gpu=device.Gpu;
        return TimeEstimates.Key("estimate-v4-fp32",engineFingerprint,device.Device=="cpu"?"ncnn-cpu":"ncnn-vulkan",workers,hardware?.CpuModel,hardware?.LogicalProcessors,gpu?.DeviceId,gpu?.DriverVersion,
            // Tone-mapped HDR decodes slower; mark only those keys so existing SDR history stays valid.
            device.Device,device.HalfStorage,source.Width,source.Height,source.BitDepth,source.HdrInput is { } hdr?$"{source.Codec}+tonemap-{hdr.Name}":source.Codec,source.Fps.ToString(),fps.ToString(),
            // Platform/size/quality-only are folded into the encoder part so existing YouTube and quality keys stay valid.
            options.Model,encoder.Name+(options.Platform is "twitch" or "chzzk"?"/"+options.Platform:"")+(options.OutputHeight is int h?"@"+h:"")+(options.QualityOnly&&options.Platform is not null?"+cq":"")+(options.MatchSource?"+src":""),
            options.Quality,options.Bitrate,options.Youtube,options.Deduplicate,options.Hud.Length,options.ScaleSourceBitrate,source.VideoBitrate,source.OverallBitrate,string.Join(' ',EncoderPolicy.Arguments(encoder,options.Quality,options.Bitrate)));
    }
    public async Task<TimeEstimate> EstimateAsync(AnalysisResult analysis,ConversionOptions options,CancellationToken cancel=default,bool refresh=false){
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel);deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try{return await MeasureEstimateAsync(analysis,options,deadline.Token,refresh);}
        catch(OperationCanceledException)when(!cancel.IsCancellationRequested){throw new TimeoutException("시간 측정 제한 초과 · CPU 또는 느린 장치는 더 오래 걸릴 수 있습니다.");}
    }
    private async Task<TimeEstimate> MeasureEstimateAsync(AnalysisResult analysis,ConversionOptions options,CancellationToken cancel,bool refresh=false){
        var source=analysis.Source;var devices=await HardwareAsync(cancel);
        Rational fps=options.Fps??Timeline.DefaultRate(source.IsVfr?source.NominalFps:source.Fps);
        if(fps<=0||fps>1000||options.Platform is not null&&fps>60)throw new ArgumentException("선택한 플랫폼 프리셋은 60fps까지 지원합니다.");
        ulong required=EngineWorkerPolicy.RequiredGpuBytes(source);
        var device=DevicePolicy.Choose(devices.Hardware.Gpus,options.Device,required);
        if(source.BitDepth>8)device=device with{HalfStorage=false};
        var (codec,bitrate)=OutputPresets.Encoding(source,options,devices.Encoders,fps);
        var encoder=EncoderPolicy.Choose(devices.Encoders,codec,options.Encoder,source.BitDepth>8,options.HardwareEncoding).Encoder;
        if(!options.HighQualityEncoding)encoder=encoder with{HighQualityTuning=false};
        int workers=EngineWorkerPolicy.For(source,device);
        string key=PerformanceKey(source,options,device,encoder,fps,workers);
        if(!refresh&&TimeEstimates.Load(key) is {} cached)return TimeEstimates.Predict(cached,analysis,fps,options.Verify);
        byte[]? first=null,second=null;Signature? before=null;
        await foreach(var frame in Decoder.WindowAsync(paths,source,analysis.BestSampleOffset,8,cancel)){using(frame){
            var signature=Signature.Create(frame,source);
            if(first is null){first=frame.Memory.ToArray();before=signature;}
            else if(before!.Difference(signature)>.2f){second=frame.Memory.ToArray();break;}
        }}
        if(first is null)throw new IOException("No sample available for time estimate");second??=first;
        double infer=0;
        // Measure real full-size transfer/inference; the tiny startup probe is not a speed forecast.
        await using(var pool=new EnginePool(workers,device,d=>new NativeEngine(paths,options.Model,source.Width,source.Height,d,source.Height>1440?.5:1,source.BitDepth>8),options.Device=="auto")){
            Task<byte[]>[] Batch()=>Enumerable.Range(0,workers).Select(_=>pool.InterpolateAsync(first,second,.5f,cancel)).ToArray();
            await Task.WhenAll(Batch());
            workers=pool.Count;device=pool.Choice;
            var timings=new List<double>();
            for(int i=0;i<(device.Device=="cpu"?1:3);i++){var clock=Stopwatch.StartNew();await Task.WhenAll(Batch());timings.Add(clock.Elapsed.TotalSeconds/workers);}
            infer=TimeEstimates.Median(timings);
            workers=pool.Count;device=pool.Choice;
        }
        key=PerformanceKey(source,options,device,encoder,fps,workers);
        string parent=Path.GetFullPath(Path.Combine(EncoderDetector.WorkRoot,"estimates"));Directory.CreateDirectory(parent);
        string folder=Path.Combine(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        double encode;
        try{
            var header=source with{Pts=[0],Audio=[]};var clock=Stopwatch.StartNew();double warm=0;
            await using(var writer=new VideoEncoder(paths,Path.Combine(folder,"probe.mp4"),header,fps,encoder,options.Quality,
                bitrate,
                options.Platform,(Rational)24/fps,options.OutputHeight)){
                for(int i=0;i<24;i++){await writer.WriteAsync(i%2==0?first:second,cancel);if(i==3)warm=clock.Elapsed.TotalSeconds;}
                await writer.CompleteAsync(cancel);encode=Math.Max(.000001,(clock.Elapsed.TotalSeconds-warm)/20);
            }
        }catch(EncodingException)when(encoder.HighQualityTuning){
            return await MeasureEstimateAsync(analysis,options with{HighQualityEncoding=false},cancel,refresh);
        }catch(EncodingException)when(options.Encoder=="auto"&&options.HardwareEncoding&&encoder.Hardware){
            return await MeasureEstimateAsync(analysis,options with{HardwareEncoding=false},cancel,refresh);
        }finally{
            string resolved=Path.GetFullPath(folder);
            if(Path.GetDirectoryName(resolved)==parent)Directory.Delete(resolved,true);
        }
        var profile=new PerformanceProfile(key,device.Gpu?.Name??"CPU",encoder.Name,Math.Max(.000001,analysis.DecodeSecondsPerFrame),infer,encode,DateTime.UtcNow);
        TimeEstimates.Save(profile);return TimeEstimates.Predict(profile,analysis,fps,options.Verify);
    }
    private void RecordPerformance(MediaInfo source,ConversionOptions options,DeviceChoice device,EncoderCapability encoder,long output,long interpolated,double seconds,int workers){
        try{if(options.MaxFrames is null)TimeEstimates.Observe(PerformanceKey(source,options,device,encoder,options.Fps??Timeline.DefaultRate(source.IsVfr?source.NominalFps:source.Fps),workers),source.FrameCount,output,interpolated,options.Verify,seconds,source.Duration);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
    }
}
