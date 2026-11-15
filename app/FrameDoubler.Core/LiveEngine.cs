using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace FrameDoubler.Core;

// Live capture preview: a thin wrapper around engine/fdlive.dll (poc/live). The engine owns
// capture, duplicate removal, anti-aliasing, frame generation, presentation and recording;
// this side only lists devices, passes settings and reads the status once in a while.
public sealed record CaptureMode(int Width,int Height,int FpsNum,int FpsDen,string Format,bool Supported)
{
    public double Fps=>FpsDen>0?(double)FpsNum/FpsDen:0;
    public string Key=>$"{Width}x{Height}@{FpsNum}/{FpsDen} {Format}";
    public string Label=>$"{Width}×{Height} · {Fps.ToString(Fps%1==0?"0":"0.##",CultureInfo.InvariantCulture)}fps · {Format}";
}
public sealed record CaptureDevice(string Name,string Id,string Error,IReadOnlyList<CaptureMode> Modes)
{
    // The best default: the largest picture, then the highest rate, NV12 before YUY2.
    public CaptureMode? PreferredMode=>Modes.Where(m=>m.Supported).OrderByDescending(m=>m.Width*m.Height).ThenByDescending(m=>Math.Round(m.Fps,2)<=60.01?m.Fps:0)
        .ThenByDescending(m=>m.Fps).ThenBy(m=>m.Format=="NV12"?0:1).FirstOrDefault();
}
public sealed record AudioInput(string Id,string Name,bool Default);
public sealed record CaptureInventory(IReadOnlyList<CaptureDevice> Video,IReadOnlyList<AudioInput> Audio)
{
    public IReadOnlyList<AudioInput> AudioOutputs {get;init;}=[];
    // Capture cards expose "<name>-Video" and an audio endpoint containing "<name>".
    // The card's own audio input: its name contains the card name (AVerMedia "...-Video" /
    // "HDMI/Line In(...-Audio)", Elgato "Digital Audio Interface (<card>)", Magewell), otherwise
    // the input sharing the most distinctive words with it. Generic words alone never match, so
    // a webcam does not pick a microphone or another card's input by "USB" or "HDMI".
    private static readonly HashSet<string> GenericWords=new(StringComparer.OrdinalIgnoreCase){"video","audio","capture","usb","usb2","usb3","hdmi","digital","interface","device","line","in","input","microphone","mic","the","and","with","stream","streaming","camera","webcam","game","4k","1080p","hd","pro"};
    private static string[] Words(string text)=>System.Text.RegularExpressions.Regex.Split(text,@"[^\p{L}\p{N}+.]+").Where(x=>x.Length>=2&&!GenericWords.Contains(x)).ToArray();
    public AudioInput? MatchingAudio(CaptureDevice device){
        string name=device.Name;
        foreach(string suffix in new[]{"-Video"," Video"," (Video)"})if(name.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))name=name[..^suffix.Length];
        var words=Words(name);
        if(words.Length==0)return null;   // "USB Video" alone says nothing about which input belongs to it
        if(Audio.FirstOrDefault(a=>a.Name.Contains(name,StringComparison.OrdinalIgnoreCase)) is {} direct)return direct;
        var best=Audio.Select(a=>(a,score:Words(a.Name).Count(w=>words.Contains(w,StringComparer.OrdinalIgnoreCase)))).Where(x=>x.score>0)
            .OrderByDescending(x=>x.score).ToList();
        return best.Count>0&&(best.Count==1||best[0].score>best[1].score)?best[0].a:null;
    }
}

public sealed record LiveStages(double? Upload,double? Motion,double? Interpolate,double? Present,double? Encode)
{
    public double Total=>(Upload??0)+(Motion??0)+(Interpolate??0)+(Present??0)+(Encode??0);
}
public sealed record LiveEvent(string Kind,string Message);
public sealed record LiveStatus(bool Running,bool CaptureFailed,int Width,int Height,string Format,double ModeFps,string Gpu,
    double InputFps,double ContentFps,double OutputFps,double InterpolatedFps,double PresentedFps,int DisplayHz,bool Tearing,double? LatencyMs,LiveStages Stages,string FrameGeneration,
    string VfxRuntime,int AudioDelayMs,bool Audio,long Input,long Unique,long Duplicates,long Dropped,
    bool Recording,string RecordingPath,double RecordingSeconds,long RecordingDropped,IReadOnlyList<LiveEvent> Events)
{
    public double TargetFps {get;init;}
    public string GpuLuid {get;init;}="";
    public int DlssMaxMultiplier {get;init;}
    // The multiplier actually run (1 = no generator; 0 = older engine).
    public int Multiplier {get;init;}
    // Pictures shown per second that differ from the one before (repeats not counted);
    // 0 = older engine, which reported only Present calls.
    public double DistinctFps {get;init;}
    public double ShownFps=>DistinctFps>0?DistinctFps:PresentedFps;
    // Of those, generated pictures per second (whether interpolation really takes place).
    public double DistinctGeneratedFps {get;init;}
    // Capture callback of an original -> the vblank that showed it (null = not measured / older engine).
    public double? ShownLatencyMs {get;init;}
    public string DlssMotion {get;init;}="";
    public bool EdgeAegis {get;init;}
    public string MotionWarning {get;init;}="";
    // Colour actually used: "limited"/"full", "709"/"601", each with "manual", "device" or "default" as its source.
    public string ColorRange{get;init;}="";public string ColorRangeSource{get;init;}="";public string ColorMatrix{get;init;}="";public string ColorMatrixSource{get;init;}="";
    public double AudioInputPeak {get;init;}
    public double AudioOutputPeak {get;init;}
    public long AudioInputFrames {get;init;}
    public long AudioOutputFrames {get;init;}
    public string CaptureState {get;init;}="";
    public string AudioInputState {get;init;}="";
    public string AudioOutputState {get;init;}="";
    public string AudioOutputId {get;init;}="";
    public string AudioOutputName {get;init;}="";
    public int AudioQueueMs {get;init;}
    public int AudioBufferMs {get;init;}
    public long AudioOutputAttempts {get;init;}
    public long AudioUnderruns {get;init;}
    public bool RecordingFinalizing {get;init;}
    public bool RecordingStarting {get;init;}
    public double RecordingFps {get;init;}
    public static LiveStatus Parse(string json){
        using var document=JsonDocument.Parse(json);var r=document.RootElement;
        static double? N(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number?v.GetDouble():null;
        static string S(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
        static bool B(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
        var stages=r.GetProperty("stages");var totals=r.GetProperty("totals");var recording=r.GetProperty("recording");
        var events=new List<LiveEvent>();
        if(r.TryGetProperty("events",out var list))foreach(var e in list.EnumerateArray())events.Add(new(S(e,"kind"),S(e,"message")));
        return new(B(r,"running"),B(r,"captureFailed"),(int)(N(r,"width")??0),(int)(N(r,"height")??0),S(r,"format"),N(r,"modeFps")??0,S(r,"gpu"),
            N(r,"inputFps")??0,N(r,"contentFps")??0,N(r,"outputFps")??0,N(r,"interpolatedFps")??0,N(r,"presentedFps")??0,(int)(N(r,"displayHz")??0),B(r,"tearing"),N(r,"latencyMs"),
            new(N(stages,"upload"),N(stages,"motion"),N(stages,"interpolate"),N(stages,"present"),N(stages,"encode")),S(r,"fg"),S(r,"vfxRuntime"),
            (int)(N(r,"audioDelayMs")??0),B(r,"audio"),(long)(N(totals,"input")??0),(long)(N(totals,"unique")??0),(long)(N(totals,"duplicates")??0),(long)(N(totals,"dropped")??0),
            B(recording,"active"),S(recording,"path"),N(recording,"seconds")??0,(long)(N(recording,"dropped")??0),events){TargetFps=N(r,"targetFps")??0,
                GpuLuid=S(r,"gpuLuid"),DlssMaxMultiplier=(int)(N(r,"dlssMaxMultiplier")??0),Multiplier=(int)(N(r,"multiplier")??0),DistinctFps=N(r,"distinctFps")??0,DistinctGeneratedFps=N(r,"distinctGeneratedFps")??0,ShownLatencyMs=N(r,"shownLatencyMs"),DlssMotion=S(r,"dlssMotion"),EdgeAegis=B(r,"edgeAegis"),MotionWarning=S(r,"motionWarning"),ColorRange=S(r,"colorRange"),ColorRangeSource=S(r,"colorRangeSource"),ColorMatrix=S(r,"colorMatrix"),ColorMatrixSource=S(r,"colorMatrixSource"),AudioInputPeak=N(r,"audioInputPeak")??0,AudioOutputPeak=N(r,"audioOutputPeak")??0,AudioInputFrames=(long)(N(r,"audioInputFrames")??0),AudioOutputFrames=(long)(N(r,"audioOutputFrames")??0),
                CaptureState=S(r,"captureState"),AudioInputState=S(r,"audioInputState"),AudioOutputState=S(r,"audioOutputState"),AudioOutputId=S(r,"audioOutputId"),AudioOutputName=S(r,"audioOutputName"),
                AudioQueueMs=(int)(N(r,"audioQueueMs")??0),AudioBufferMs=(int)(N(r,"audioBufferMs")??0),AudioOutputAttempts=(long)(N(r,"audioOutputAttempts")??0),AudioUnderruns=(long)(N(r,"audioUnderruns")??0),
                RecordingFinalizing=B(recording,"finalizing"),RecordingStarting=B(recording,"starting"),RecordingFps=N(recording,"fps")??0};
    }
}

// Motion options are selected at DLSS creation; CaptureView restarts the preview to apply them.
public sealed record LiveOptions(string FrameGeneration="off",int Multiplier=2,int AntiAlias=0,bool Dedup=false,string Range="auto",
    string Matrix="auto",int VfxQuality=1,int Volume=100,bool Mute=false,int AudioOffsetMs=0,
    string Sync="auto",string Aspect="fit",string Compare="off",string OutputCap="off",string DlssMotion="none",bool EdgeAegis=false)
{
    // Live capture generates frames with NVIDIA DLSS or NVIDIA VFX only; FrameDoubler's own
    // interpolation is for file conversion (user decision 2026-11-15). "auto" (and an "own"
    // saved by an earlier version) prefers DLSS, then a downloaded VFX. The engine hands DLSS
    // over to VFX when the driver cannot run it or the multiplier, and shows the original
    // pictures when neither can run.
    public string EngineFrameGeneration(bool vfxInstalled,bool dlssInstalled=false)=>FrameGeneration switch{"off"=>"off","vfx"=>"vfx","dlss"=>"dlss",
        _=>dlssInstalled?"dlss":"vfx"};
    public string Commands(bool vfxInstalled,bool dlssInstalled=false)=>string.Join('\n',
        "fg="+EngineFrameGeneration(vfxInstalled,dlssInstalled),"multiplier="+Multiplier,"aa="+AntiAlias,"dedup="+(Dedup?"1":"0"),"range="+Range,"matrix="+Matrix,
        "vfxQuality="+VfxQuality,"volume="+Volume,"mute="+(Mute?"1":"0"),"audioOffsetMs="+AudioOffsetMs,
        "sync="+Sync,"aspect="+Aspect,"compare="+Compare,"outputCap="+OutputCap,
        "dlssMotion="+(DlssMotion=="nvof"?"nvof":"none"),"edgeAegis="+(DlssMotion=="nvof"&&EdgeAegis?"1":"0"));
}

public static class LiveEngine
{
    public const string Library="fdlive";
    private static string? directory;
    private static int registered;
    // engine/ next to the other helpers; FRAMEDOUBLER_LIVE_DIR overrides it for development.
    public static string Directory=>directory??=Environment.GetEnvironmentVariable("FRAMEDOUBLER_LIVE_DIR") is {Length:>0} dir?Path.GetFullPath(dir)
        :Path.GetDirectoryName(AppPaths.Discover().Engine)!;
    public static string LibraryPath=>Path.Combine(Directory,"fdlive.dll");
    // A development override, then a copy bundled in engine/nvvfx, then the copy the user downloaded.
    public static string VfxDirectory{get{
        if(Environment.GetEnvironmentVariable("FRAMEDOUBLER_VFX_DIR") is {Length:>0} dir)return Path.GetFullPath(dir);
        string bundled=Path.Combine(Directory,"nvvfx");
        return VfxRuntime.Complete(bundled)?bundled:VfxRuntime.DownloadDirectory(Directory);}}
    public static bool Available=>File.Exists(LibraryPath);
    public static bool VfxInstalled=>VfxRuntime.Complete(VfxDirectory);
    // DLSS frame generation feature library (engine/dlss); the NGX core itself comes with the NVIDIA driver.
    public static string DlssDirectory=>Environment.GetEnvironmentVariable("FRAMEDOUBLER_DLSS_DIR") is {Length:>0} dir?Path.GetFullPath(dir):Path.Combine(Directory,"dlss");
    public static bool DlssInstalled=>File.Exists(Path.Combine(DlssDirectory,"nvngx_dlssg.dll"));
    // DLSS runs inside NVIDIA's driver component, where a fault cannot be caught. A marker is
    // written while a session uses DLSS and removed when that session closes normally; a marker
    // left behind means the program ended abnormally, so "auto" stops choosing DLSS until the
    // user selects it explicitly again.
    private static bool dlssAttempt;
    private static string DlssMarker=>Path.Combine(Preferences.DirectoryPath,"dlss-attempt.txt");
    public static bool DlssBlocked=>!dlssAttempt&&File.Exists(DlssMarker);
    public static bool DlssAutomatic=>DlssInstalled&&!DlssBlocked;
    internal static void BeginDlssAttempt(LiveOptions options){
        if(options.EngineFrameGeneration(VfxInstalled,DlssAutomatic)!="dlss"||!DlssInstalled)return;
        try{File.WriteAllText(DlssMarker,"DLSS frame generation was in use. This file is removed when the preview closes normally.\n");dlssAttempt=true;}catch(Exception){}
    }
    internal static void EndDlssAttempt(){
        if(!dlssAttempt)return;
        try{File.Delete(DlssMarker);}catch(Exception){}
        dlssAttempt=false;
    }
    public static void ClearDlssBlock(){try{File.Delete(DlssMarker);}catch(Exception){}}
    // Largest multiplier the frame generation choices offer on this PC: 2, or what the driver's
    // DLSS-G reports when it allows more (multi-frame generation, e.g. RTX 50 series). Decided by
    // the driver's capability, never by GPU names. The query runs inside the driver, so it runs in
    // the engine's command-line helper: a fault there only means "2x" and cannot close the program.
    public static string HelperPath=>Path.Combine(Directory,"fd-live.exe");
    public static int ProbeMaxMultiplier(){
        if(!Available||!DlssInstalled||DlssBlocked||!File.Exists(HelperPath))return 2;
        try{
            var start=new System.Diagnostics.ProcessStartInfo(HelperPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string a in new[]{"--dlss-probe","--dlss",DlssDirectory,"--dlss-data",NgxDataDirectory()})start.ArgumentList.Add(a);
            using var process=System.Diagnostics.Process.Start(start);
            if(process is null)return 2;
            var output=process.StandardOutput.ReadToEndAsync();_=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(20000)){try{process.Kill(true);}catch(Exception){}return 2;}
            if(process.ExitCode!=0)return 2;
            string text=output.Result;int at=text.LastIndexOf("{\"maxMultiplier\"",StringComparison.Ordinal);
            if(at<0)return 2;
            using var document=JsonDocument.Parse(text[at..]);
            return Math.Clamp(document.RootElement.GetProperty("maxMultiplier").GetInt32(),2,4);
        }catch(Exception){return 2;}
    }
    private static void Register(){
        if(Interlocked.Exchange(ref registered,1)!=0)return;
        NativeLibrary.SetDllImportResolver(typeof(LiveEngine).Assembly,(name,assembly,search)=>
            name==Library&&File.Exists(LibraryPath)?NativeLibrary.Load(LibraryPath):IntPtr.Zero);
    }
    private static string Take(IntPtr text){
        if(text==IntPtr.Zero)return "";
        try{return Marshal.PtrToStringUTF8(text)??"";}finally{Native.fdl_free(text);}
    }
    public static string DevicesJson(){Register();return Take(Native.fdl_devices());}
    public static CaptureInventory Devices(){
        Register();
        using var document=JsonDocument.Parse(Take(Native.fdl_devices()));
        var video=new List<CaptureDevice>();var audio=new List<AudioInput>();
        foreach(var d in document.RootElement.GetProperty("video").EnumerateArray()){
            var modes=d.GetProperty("modes").EnumerateArray().Select(m=>new CaptureMode(m.GetProperty("width").GetInt32(),m.GetProperty("height").GetInt32(),
                m.GetProperty("fpsNum").GetInt32(),m.GetProperty("fpsDen").GetInt32(),m.GetProperty("format").GetString()??"",m.GetProperty("supported").GetBoolean())).ToList();
            video.Add(new(d.GetProperty("name").GetString()??"",d.GetProperty("id").GetString()??"",d.GetProperty("error").GetString()??"",modes));
        }
        foreach(var a in document.RootElement.GetProperty("audio").EnumerateArray())
            audio.Add(new(a.GetProperty("id").GetString()??"",a.GetProperty("name").GetString()??"",a.GetProperty("default").GetBoolean()));
        var outputs=new List<AudioInput>();
        if(document.RootElement.TryGetProperty("audioOutputs",out var devices))foreach(var a in devices.EnumerateArray())
            outputs.Add(new(a.GetProperty("id").GetString()??"",a.GetProperty("name").GetString()??"",a.GetProperty("default").GetBoolean()));
        return new(video,audio){AudioOutputs=outputs};
    }
    public static string SelfTest(string? vfxDirectory=null){
        Register();
        return Take(Native.fdl_selftest(vfxDirectory is null?"":"vfxDir="+vfxDirectory+"\n"));
    }
    // NGX writes its own log files; they go to the work folder, never into the package.
    private static string NgxDataDirectory(){
        try{string path=Path.Combine(EncoderDetector.WorkRoot,"ngx");System.IO.Directory.CreateDirectory(path);return path;}catch(Exception){return "";}
    }
    public static LiveSession Open(CaptureDevice device,CaptureMode mode,string? audioId,LiveOptions options,string screenshotDirectory,
        string recordingDirectory,string recordingCodec,int recordingBitrate,IntPtr window,string? audioOutput=null){
        Register();
        var settings=new StringBuilder();
        void Add(string key,object value){string text=Convert.ToString(value,CultureInfo.InvariantCulture)??"";
            if(text.Contains('\n')||text.Contains('\r'))throw new ArgumentException("Setting must be one line: "+key);settings.Append(key).Append('=').Append(text).Append('\n');}
        Add("device",device.Id);Add("width",mode.Width);Add("height",mode.Height);Add("fpsNum",mode.FpsNum);Add("fpsDen",mode.FpsDen);Add("format",mode.Format);
        Add("audio",audioId??"none");Add("vfxDir",VfxDirectory);Add("dlssDir",DlssInstalled?DlssDirectory:"");Add("dlssDataDir",NgxDataDirectory());Add("screenshotDir",screenshotDirectory);Add("recordDir",recordingDirectory);
        Add("audioOutput",audioOutput is null or "default"?"":audioOutput);
        Add("recordCodec",recordingCodec);Add("recordBitrate",recordingBitrate);
        // Development validation only (tools/validation/live-gui): native timing traces.
        if(Environment.GetEnvironmentVariable("FRAMEDOUBLER_PACING_TRACE") is {Length:>0} pacingTrace)Add("pacingTrace",pacingTrace);
        if(Environment.GetEnvironmentVariable("FRAMEDOUBLER_AUDIO_TRACE") is {Length:>0} audioTrace)Add("audioTrace",audioTrace);
        BeginDlssAttempt(options);
        settings.Append(options.Commands(VfxInstalled,DlssAutomatic)).Append('\n');
        IntPtr handle=Native.fdl_open(settings.ToString(),window,out IntPtr error);
        if(handle==IntPtr.Zero)EndDlssAttempt();
        if(handle==IntPtr.Zero)throw new InvalidOperationException(Take(error) is {Length:>0} message?message:"capture could not start");
        return new LiveSession(handle);
    }
    internal static class Native
    {
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] public static extern int fdl_api_version();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr fdl_devices();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr fdl_open([MarshalAs(UnmanagedType.LPUTF8Str)] string settings,IntPtr window,out IntPtr error);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] public static extern int fdl_command(IntPtr session,[MarshalAs(UnmanagedType.LPUTF8Str)] string command);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr fdl_status(IntPtr session);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] public static extern void fdl_close(IntPtr session);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr fdl_selftest([MarshalAs(UnmanagedType.LPUTF8Str)] string settings);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr fdl_dlss_probe([MarshalAs(UnmanagedType.LPUTF8Str)] string settings);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] public static extern void fdl_free(IntPtr text);
    }
    internal static string TakeText(IntPtr text)=>Take(text);
}

public sealed class LiveSession:IDisposable
{
    private IntPtr handle;
    private readonly object gate=new();
    internal LiveSession(IntPtr handle)=>this.handle=handle;
    public bool Open=>handle!=IntPtr.Zero;
    public void Apply(LiveOptions options){LiveEngine.BeginDlssAttempt(options);Command(options.Commands(LiveEngine.VfxInstalled,LiveEngine.DlssAutomatic));}
    public void Command(string command){lock(gate)if(handle!=IntPtr.Zero)LiveEngine.Native.fdl_command(handle,command);}
    public void Screenshot()=>Command("action=screenshot");
    public void SetAudioOutput(string id){
        if(id.Contains('\n')||id.Contains('\r'))throw new ArgumentException("Output endpoint must be one line",nameof(id));
        Command("audioOutput="+id);
    }
    public void StartRecording()=>Command("action=record-start");
    public void StopRecording()=>Command("action=record-stop");
    public string? StatusJson(){
        lock(gate){if(handle==IntPtr.Zero)return null;return LiveEngine.TakeText(LiveEngine.Native.fdl_status(handle));}
    }
    public LiveStatus? Status(){
        string json;
        lock(gate){if(handle==IntPtr.Zero)return null;json=LiveEngine.TakeText(LiveEngine.Native.fdl_status(handle));}
        return json.Length==0?null:LiveStatus.Parse(json);
    }
    public void Dispose(){
        IntPtr h;
        lock(gate){h=handle;handle=IntPtr.Zero;}
        // Closing joins the engine's threads and finishes a recording; never on a UI thread lock.
        if(h!=IntPtr.Zero){LiveEngine.Native.fdl_close(h);LiveEngine.EndDlssAttempt();}
    }
}
