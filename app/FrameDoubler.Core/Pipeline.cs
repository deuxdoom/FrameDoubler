using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FrameDoubler.Core;

public sealed record ConversionOptions
{
    public Rational? Fps{get;init;}
    public string Model{get;init;}="v4.25";
    public string Device{get;init;}="auto";
    public string Encoder{get;init;}="auto";
    public string? Codec{get;init;}
    public bool Deduplicate{get;init;}=true;
    public bool Youtube{get;init;}
    public int Quality{get;init;}=19;
    public string? Bitrate{get;init;}
    public bool Verify{get;init;}=true;
    public bool Overwrite{get;init;}
    public bool HardwareEncoding{get;init;}=true;
    public float SceneThreshold{get;init;}=.35f;
    public int? MaxFrames{get;init;}
    public HudRegion[] Hud{get;init;}=[];
}
public sealed record ConversionProgress(string Stage,long Frames,long Total,double Seconds,string Message="")
{
    public double Fraction=>Total>0?(double)Frames/Total:0;
    public double? Remaining=>Frames>0?Seconds*(Total-Frames)/Frames:null;
}
public sealed record SampleInterval(Rational First,Rational Second,bool Hold);
public sealed record AnalysisResult(MediaInfo Source,int Unique,int Duplicates,int Static,double? MotionFps){
    public int SampledFrames{get;init;}
    public double DecodeSecondsPerFrame{get;init;}
    public double BestSampleOffset{get;init;}
    public SampleInterval[] Intervals{get;init;}=[];
    public bool IsSampled=>SampledFrames>0&&SampledFrames<Source.FrameCount;
}
public sealed record ConversionResult(string Output,Rational Fps,long Frames,int Interpolated,int SceneCuts,int Duplicates,
    double Seconds,string Device,string Encoder,VerificationReport? Verification){public double OverallSeconds{get;init;}}

public sealed partial class ConversionPipeline(AppPaths paths)
{
    private HardwareInventory? hardware;
    private List<EncoderCapability>? encoders;
    private readonly Dictionary<string,DeviceChoice> calibration=new();
    public async Task<(HardwareInventory Hardware,List<EncoderCapability> Encoders)> HardwareAsync(CancellationToken cancel=default)
    {
        hardware??=await HardwareDetector.DetectAsync(paths,cancel:cancel);
        encoders??=await EncoderDetector.DetectAsync(paths,cancel);
        return (hardware,encoders);
    }
    public async Task<AnalysisResult> AnalyzeAsync(string input,IProgress<ConversionProgress>? progress=null,CancellationToken cancel=default)
    {
        var info=await MediaProbe.ReadAsync(paths,input,cancel,metadataOnly:true);
        return await SampleAnalysisAsync(info,cancel);
    }
    public async Task<ConversionResult> RunAsync(string input,string output,ConversionOptions options,
        IProgress<ConversionProgress>? progress=null,CancellationToken cancel=default)
    {
        var overall=Stopwatch.StartNew();
        if(options.Quality is <1 or >51||options.SceneThreshold is <0 or >1||options.MaxFrames is <=0)throw new ArgumentException("Invalid conversion settings");
        if(options.Model is not ("v4.25" or "v4.26"))throw new ArgumentException("Unsupported RIFE model");
        if(options.Bitrate is not null&&!System.Text.RegularExpressions.Regex.IsMatch(options.Bitrate,@"^\d+[kKmM]?$"))throw new ArgumentException("Invalid bitrate");
        output=Path.GetFullPath(output);input=Path.GetFullPath(input);
        if(string.Equals(input,output,StringComparison.OrdinalIgnoreCase))throw new IOException("Input and output paths must differ");
        if(File.Exists(output)&&!options.Overwrite)throw new IOException("Output already exists");
        if(!string.Equals(Path.GetExtension(output),".mp4",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Output must be .mp4");
        progress?.Report(new("analyze",0,0,0));
        var source=await MediaProbe.ReadAsync(paths,input,cancel);
        if(source.HdrInput is { } hdr)progress?.Report(new("analyze",0,0,0,ToneMapNotice(hdr)));
        foreach(var roi in options.Hud)roi.Validate(source);
        Rational fps=options.Fps??Timeline.DefaultRate(source.IsVfr?source.NominalFps:source.Fps);
        if(fps<=0||fps>1000)throw new ArgumentException("Invalid output frame rate");
        if(options.Youtube&&fps>60)throw new ArgumentException("YouTube preset supports up to 60fps");
        int limit=Math.Min(options.MaxFrames??source.Pts.Length,source.Pts.Length);
        Rational duration=limit<source.Pts.Length?(Rational)(source.Pts[limit]-source.Pts[0])*source.TimeBase:source.Duration;
        long total=(duration*fps).Ceiling();
        progress?.Report(new("hardware",0,total,0,"장치와 인코더를 확인합니다."));
        var devices=await HardwareAsync(cancel);
        // Conservative working-set estimate, not a claim of exact VRAM consumption.
        ulong required=checked((ulong)source.Width*(ulong)source.Height*(source.BitDepth>8?768UL:384UL));
        var choice=DevicePolicy.Choose(devices.Hardware.Gpus,options.Device,required);
        if(source.BitDepth>8&&choice.Gpu is not null)choice=choice with{HalfStorage=false,Explanation=$"{choice.Gpu.Name} / Vulkan / FP32 저장·연산 (고비트 심도)"};
        string codec=options.Codec??EncoderPolicy.DefaultCodec(devices.Encoders,options.Youtube,source.BitDepth>8,options.HardwareEncoding);
        var encoder=EncoderPolicy.Choose(devices.Encoders,codec,options.Encoder,source.BitDepth>8,options.HardwareEncoding);
        progress?.Report(new("model",0,total,0,choice.Explanation+" · "+encoder.Explanation));
        string? bitrate=options.Bitrate??(options.Youtube?YoutubeBitrate(source.Width,source.Height,fps):null);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temp=Path.Combine(Path.GetDirectoryName(output)!,"."+Path.GetFileNameWithoutExtension(output)+$".{Guid.NewGuid():N}.partial.mp4");
        NativeEngine? engine=null;VideoFrame? left=null;float[]? leftScene=null;
        bool calibrated=options.Device!="auto"||devices.Hardware.Gpus.Count(x=>x.InferencePassed==true&&x.SupportsWarp)<=1;
        int interpolated=0,cuts=0,duplicates=0;var watch=Stopwatch.StartNew();double lastReport=-1;
        VerificationReport? verification=null;
        try {
            await using(var writer=new VideoEncoder(paths,temp,source,fps,encoder.Encoder,options.Quality,bitrate,options.Youtube,duration)) {
                await foreach(var item in LabelledAsync(source,limit,options.Deduplicate,cancel)){
                    var frame=item.Frame;bool retained=false;
                    try {
                        cancel.ThrowIfCancellationRequested();
                        if(item.Label==FrameLabel.Duplicate){duplicates++;continue;}
                        float[] scene=item.Signature.Descriptor();
                        if(left is not null){
                            bool hold=item.Label==FrameLabel.Static;
                            if(!hold&&Signature.CutScore(leftScene!,scene)>options.SceneThreshold){hold=true;cuts++;}
                            foreach(var (_,position) in Timeline.Steps(left.Time,frame.Time,fps)){
                                cancel.ThrowIfCancellationRequested();
                                if(position==0 || (hold&&position<1))await writer.WriteAsync(left.Memory,cancel);
                                else if(position==1)await writer.WriteAsync(frame.Memory,cancel);
                                else {
                                    if(!calibrated){
                                        progress?.Report(new("hardware",writer.FramesWritten,total,watch.Elapsed.TotalSeconds,"현재 영상 크기에서 GPU 전송·추론 성능을 비교합니다."));
                                        choice=await CalibrateAsync(devices.Hardware.Gpus,options.Model,source,left,frame,required,cancel);
                                        calibrated=true;
                                        progress?.Report(new("model",writer.FramesWritten,total,watch.Elapsed.TotalSeconds,choice.Explanation));
                                    }
                                    engine??=CreateEngine();
                                    byte[] middle;
                                    try{middle=await engine.InterpolateAsync(left.Memory,frame.Memory,position,cancel);}
                                    catch(IOException)when(options.Device=="auto"&&choice.Device!="cpu"){
                                        await engine.DisposeAsync();choice=DevicePolicy.Choose([],"cpu");engine=CreateEngine();
                                        progress?.Report(new("fallback",writer.FramesWritten,total,watch.Elapsed.TotalSeconds,choice.Explanation));
                                        middle=await engine.InterpolateAsync(left.Memory,frame.Memory,position,cancel);
                                    }
                                    foreach(var roi in options.Hud)roi.Restore(left,frame,middle,source);
                                    await writer.WriteAsync(middle,cancel);interpolated++;
                                }
                                if(watch.Elapsed.TotalSeconds-lastReport>=.25){lastReport=watch.Elapsed.TotalSeconds;progress?.Report(new("convert",writer.FramesWritten,total,lastReport));}
                            }
                        }
                        left?.Dispose();left=frame;leftScene=scene;retained=true;
                    }finally{if(!retained)frame.Dispose();}
                }
                if(left is null)throw new IOException("No decoded frames");
                while(writer.FramesWritten<total){cancel.ThrowIfCancellationRequested();await writer.WriteAsync(left.Memory,cancel);}
                if(writer.FramesWritten!=total)throw new IOException("Output timeline has the wrong number of frames");
                progress?.Report(new("mux",total,total,watch.Elapsed.TotalSeconds));
                await writer.CompleteAsync(cancel);
            }
            if(options.Verify&&limit==source.Pts.Length){
                progress?.Report(new("verify",total,total,watch.Elapsed.TotalSeconds));
                verification=await Verifier.VerifyAsync(paths,input,temp,fps,cancel);
                // Duplicate detection is a quality heuristic. Keep a valid file for
                // review with an explicit failed/warning report, as in the baseline.
                // Structural timestamp/audio failures still prevent finalization.
                if(verification.Problems.Any(x=>!x.Contains("moving frames remain duplicates",StringComparison.Ordinal)))
                    throw new IOException("Output verification failed: "+string.Join("; ",verification.Problems));
            }
            cancel.ThrowIfCancellationRequested();
            File.Move(temp,output,options.Overwrite);
            progress?.Report(new(verification?.Ok==false?"warning":"done",total,total,watch.Elapsed.TotalSeconds,
                choice.Explanation+" · "+encoder.Explanation+(verification?.Ok==false?" · "+string.Join("; ",verification.Problems):"")));
            RecordPerformance(source,options,choice,encoder.Encoder,total,interpolated,overall.Elapsed.TotalSeconds);
            return new(output,fps,total,interpolated,cuts,duplicates,watch.Elapsed.TotalSeconds,choice.Device,encoder.Encoder.Name,verification){OverallSeconds=overall.Elapsed.TotalSeconds};
        }catch(EncodingException)when(options.Encoder=="auto"&&options.HardwareEncoding&&encoder.Encoder.Hardware){
            if(engine is not null)await engine.DisposeAsync();left?.Dispose();if(File.Exists(temp))File.Delete(temp);
            double failedSeconds=watch.Elapsed.TotalSeconds;
            progress?.Report(new("encoder_fallback",0,total,watch.Elapsed.TotalSeconds,"하드웨어 인코딩에 실패하여 소프트웨어로 다시 시도합니다. 처리 속도가 느려질 수 있습니다."));
            var retry=await RunAsync(input,output,options with{HardwareEncoding=false},progress,cancel);
            return retry with{Seconds=retry.Seconds+failedSeconds};
        }catch(OperationCanceledException){progress?.Report(new("cancelled",0,total,watch.Elapsed.TotalSeconds));throw;}
        catch(Exception ex){progress?.Report(new("error",0,total,watch.Elapsed.TotalSeconds,ex.Message));throw;}
        finally{left?.Dispose();if(engine is not null)await engine.DisposeAsync();if(File.Exists(temp))File.Delete(temp);}
        NativeEngine CreateEngine()=>new(paths,options.Model,source.Width,source.Height,choice,source.Height>1440?.5:1,source.BitDepth>8);
    }
    public static string ToneMapNotice(HdrSource hdr)=>$"HDR({hdr.Name}) 영상은 SDR(BT.709, 8비트)로 톤 매핑하여 변환합니다.";
    private static string YoutubeBitrate(int width,int height,Rational fps){
        int shortSide=Math.Min(width,height),longSide=Math.Max(width,height);
        if(shortSide<=1080&&longSide<=1920)return fps>30?"12M":"8M";
        if(shortSide<=1440&&longSide<=2560)return fps>30?"24M":"16M";
        if(shortSide<=2160&&longSide<=3840)return fps>30?"68M":"45M";
        throw new ArgumentException("YouTube preset supports up to 4K");
    }
    private async Task<DeviceChoice> CalibrateAsync(List<GpuDevice> gpus,string model,MediaInfo source,VideoFrame first,VideoFrame second,ulong required,CancellationToken cancel){
        string key=$"{model}:{source.Width}:{source.Height}:{source.BitDepth}:"+string.Join(';',gpus.Select(x=>$"{x.Index}:{x.DeviceId}:{x.DriverVersion}"));
        if(calibration.TryGetValue(key,out var cached))return cached;
        var tested=new List<GpuDevice>();
        foreach(var gpu in gpus.Where(x=>x.InferencePassed==true&&x.SupportsWarp&&(x.BudgetBytes is null||x.BudgetBytes>=required))){
            cancel.ThrowIfCancellationRequested();
            try{
                var candidate=DevicePolicy.Choose([gpu],$"gpu:{gpu.Index}",required);
                if(source.BitDepth>8)candidate=candidate with{HalfStorage=false};
                await using var worker=new NativeEngine(paths,model,source.Width,source.Height,candidate,source.Height>1440?.5:1,source.BitDepth>8);
                await worker.InterpolateAsync(first.Memory,second.Memory,.5f,cancel);
                var watch=Stopwatch.StartNew();await worker.InterpolateAsync(first.Memory,second.Memory,.5f,cancel);
                tested.Add(gpu with{MeasuredMilliseconds=watch.Elapsed.TotalMilliseconds});
            }catch(OperationCanceledException){throw;}
            catch(Exception ex)when(ex is IOException or TimeoutException){tested.Add(gpu with{InferencePassed=false,Error=ex.Message});}
        }
        var result=DevicePolicy.Choose(tested,"auto",required);
        if(source.BitDepth>8&&result.Gpu is not null)result=result with{HalfStorage=false,Explanation=$"{result.Gpu.Name} / Vulkan / FP32 저장·연산"};
        calibration[key]=result;
        try{
            string folder=Path.Combine(EncoderDetector.WorkRoot,"device-calibration");Directory.CreateDirectory(folder);
            string file=Path.Combine(folder,$"{model}-{source.Width}x{source.Height}-{source.BitDepth}.json");
            await File.WriteAllTextAsync(file,System.Text.Json.JsonSerializer.Serialize(new{Model=model,source.Width,source.Height,source.BitDepth,
                Includes="warm RGB host roundtrip, device transfers and inference",Devices=tested,Chosen=result.Device},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}),cancel);
        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        return result;
    }
    private sealed record Labelled(VideoFrame Frame,FrameLabel Label,Signature Signature);
    private async IAsyncEnumerable<Labelled> LabelledAsync(MediaInfo info,int? limit,bool dedup,
        [EnumeratorCancellation]CancellationToken cancel)
    {
        var detector=new CadenceDetector();VideoFrame? held=null;Signature? previous=null;
        try{
            await foreach(var frame in Decoder.FramesAsync(paths,info,limit,cancel)){
                Signature signature=Signature.Create(frame,info);
                if(!dedup){yield return new(frame,FrameLabel.Unique,signature);continue;}
                var label=detector.Push(signature);var old=held;var oldSignature=previous;
                held=frame;previous=signature;
                if(label.HasValue&&old is not null)yield return new(old,label.Value,oldSignature!);
            }
            var last=detector.Flush();
            if(last.HasValue&&held is not null){var frame=held;held=null;yield return new(frame,last.Value,previous!);}
        }finally{held?.Dispose();}
    }
}
