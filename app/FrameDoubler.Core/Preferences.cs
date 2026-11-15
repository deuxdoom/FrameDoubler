using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FrameDoubler.Core;

public sealed record Preferences
{
    public string Language{get;init;}="ko";
    public string OutputDirectory{get;init;}="";
    public string Preset{get;init;}="source";
    public int PresetDefaultsVersion{get;init;}=1;
    public string Fps{get;init;}="auto";
    public string Device{get;init;}="auto";
    public string Accelerator{get;init;}="auto";
    public string DeviceName{get;init;}="";
    public string Encoder{get;init;}="auto";
    public string Model{get;init;}="v4.25";
    public bool Verify{get;init;}=false;
    public bool HardwareEncoding{get;init;}=true;
    public bool CheckUpdates{get;init;}=true;
    public string CloseAction{get;init;}="ask";
    public string MinimizeAction{get;init;}="taskbar";
    // Capture preview (live engine).
    public string StartPage{get;init;}="capture";
    public string CaptureDevice{get;init;}="";
    public string CaptureDeviceId{get;init;}="";
    public string CaptureMode{get;init;}="";
    public string CaptureAudio{get;init;}="auto";
    public string CaptureAudioOutput{get;init;}="default";
    public string LiveFrameGeneration{get;init;}="off";
    public int LiveMultiplier{get;init;}=2;
    public int LiveAntiAlias{get;init;}=0;
    public bool LiveDedup{get;init;}=false;
    public string LiveRange{get;init;}="auto";
    public int LiveVfxQuality{get;init;}=1;
    public int LiveVolume{get;init;}=100;
    public bool LiveMute{get;init;}=false;
    public int LiveAudioOffset{get;init;}=0;
    public bool LiveStats{get;init;}=true;
    public string LiveMatrix{get;init;}="auto";
    public string LiveSync{get;init;}="auto";
    public string LiveAspect{get;init;}="fit";
    public string LiveOutputCap{get;init;}="off";
    public string LiveDlssMotion{get;init;}="none";
    public bool LiveEdgeAegis{get;init;}=false;
    public bool LiveHoldOriginal{get;init;}=true;
    // Empty: Pictures\FrameDoubler and Videos\FrameDoubler.
    public string ScreenshotDirectory{get;init;}="";
    public string RecordingDirectory{get;init;}="";
    public string RecordingCodec{get;init;}="h264";
    public int RecordingBitrate{get;init;}=0;
    // Capture preview shortcuts ("Ctrl+Shift+F7" style; empty = none). Global ones also work
    // while another window is in front (RegisterHotKey).
    public string ShortcutScreenshot{get;init;}="F7";
    public string ShortcutRecord{get;init;}="F8";
    public string ShortcutFullscreen{get;init;}="F11";
    public string ShortcutFrameGeneration{get;init;}="F6";
    public string ShortcutStats{get;init;}="F10";
    public string ShortcutOriginal{get;init;}="V";
    public bool GlobalShortcuts{get;init;}=false;
    public static string DefaultScreenshotDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),"FrameDoubler");
    public static string DefaultRecordingDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"FrameDoubler");
    public string EffectiveScreenshotDirectory=>string.IsNullOrWhiteSpace(ScreenshotDirectory)?DefaultScreenshotDirectory:ScreenshotDirectory;
    public string EffectiveRecordingDirectory=>string.IsNullOrWhiteSpace(RecordingDirectory)?DefaultRecordingDirectory:RecordingDirectory;
    public LiveOptions LiveOptions=>new(LiveFrameGeneration,LiveMultiplier,LiveAntiAlias,LiveDedup,LiveRange,LiveMatrix,LiveVfxQuality,LiveVolume,LiveMute,LiveAudioOffset,
        LiveSync,LiveAspect,"off",LiveOutputCap,LiveDlssMotion,LiveEdgeAegis);
    public static string DirectoryPath=>Environment.GetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR")??AppPaths.InstallationDirectory;
    public const string FileName="settings.ini";
    public static Preferences Load(string? directory=null){
        directory??=DirectoryPath;
        try{
            string file=Path.Combine(directory,FileName);
            if(File.Exists(file))return ReadIni(File.ReadAllText(file));
            string legacy=Path.Combine(directory,"settings.json");
            if(File.Exists(legacy)){
                string text=File.ReadAllText(legacy);var value=JsonSerializer.Deserialize<Preferences>(text)??new();
                using var document=JsonDocument.Parse(text);
                value=Normalize(value,document.RootElement.TryGetProperty(nameof(PresetDefaultsVersion),out _));
                // Keep the old file until the new INI has been written atomically.
                try{value.Save(directory);File.Delete(legacy);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
                return value;
            }
        }catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException or FormatException){}
        return new();
    }
    private static Preferences Normalize(Preferences value,bool defaultsVersion){
        if(!defaultsVersion&&value.Preset=="youtube")value=value with{Preset="source"};
        if(value.Preset is "youtube" or "youtube-1440")value=value with{Preset="youtube-2160"};
        if(OutputPresets.FindExact(value.Preset) is null)value=value with{Preset="source"};
        if(value.MinimizeAction!="tray")value=value with{MinimizeAction="taskbar"};
        if(value.Accelerator is not ("auto" or "vulkan" or "rtx"))value=value with{Accelerator="auto"};
        if(value.StartPage is not ("capture" or "convert"))value=value with{StartPage="capture"};
        // "own" from an earlier version: live capture no longer uses FrameDoubler interpolation.
        if(value.LiveFrameGeneration=="own")value=value with{LiveFrameGeneration="auto"};
        if(value.LiveFrameGeneration is not ("auto" or "dlss" or "vfx" or "off"))value=value with{LiveFrameGeneration="off"};
        if(value.LiveRange is not ("auto" or "limited" or "full"))value=value with{LiveRange="auto"};
        if(value.RecordingCodec is not ("h264" or "hevc"))value=value with{RecordingCodec="h264"};
        if(value.LiveMatrix is not ("auto" or "709" or "601"))value=value with{LiveMatrix="auto"};
        if(value.LiveSync is not ("auto" or "vsync" or "tearing"))value=value with{LiveSync="auto"};
        if(value.LiveAspect is not ("fit" or "fill" or "stretch"))value=value with{LiveAspect="fit"};
        if(value.LiveDlssMotion!="nvof")value=value with{LiveDlssMotion="none",LiveEdgeAegis=false};
        if(value.LiveOutputCap!="off"&&value.LiveOutputCap!="display"&&!(int.TryParse(value.LiveOutputCap,NumberStyles.Integer,CultureInfo.InvariantCulture,out int cap)&&cap is >=24 and <=480))
            value=value with{LiveOutputCap="off"};
        value=value with{LiveMultiplier=Math.Clamp(value.LiveMultiplier,2,4),LiveAntiAlias=Math.Clamp(value.LiveAntiAlias,0,3),LiveVfxQuality=Math.Clamp(value.LiveVfxQuality,0,2),
            LiveVolume=Math.Clamp(value.LiveVolume,0,100),LiveAudioOffset=Math.Clamp(value.LiveAudioOffset,-200,500),RecordingBitrate=Math.Clamp(value.RecordingBitrate,0,400)};
        return value;
    }
    private static Preferences ReadIni(string text){
        var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);bool section=false;
        foreach(string raw in text.Split('\n')){
            string line=raw.Trim();if(line.Length==0||line[0] is ';' or '#')continue;
            if(line.StartsWith('[')){section=line.Equals("[FrameDoubler]",StringComparison.OrdinalIgnoreCase);continue;}
            if(!section)continue;int equal=line.IndexOf('=');if(equal<1)continue;
            string value=line[(equal+1)..].Trim();
            if(value.StartsWith('"')){
                if(value.Length<2||!value.EndsWith('"'))throw new FormatException("Unclosed INI value");
                value=value[1..^1].Replace("\"\"","\"");
            }
            values[line[..equal].Trim()]=value;
        }
        string S(string key,string fallback)=>values.GetValueOrDefault(key,fallback);
        int I(string key,int fallback)=>int.TryParse(S(key,""),NumberStyles.Integer,CultureInfo.InvariantCulture,out var n)?n:fallback;
        bool B(string key,bool fallback)=>values.TryGetValue(key,out var value)?value.ToLowerInvariant() switch{"true" or "1" or "yes"=>true,"false" or "0" or "no"=>false,_=>fallback}:fallback;
        var result=new Preferences{
            Language=S(nameof(Language),"ko"),OutputDirectory=S(nameof(OutputDirectory),""),Preset=S(nameof(Preset),"source"),
            PresetDefaultsVersion=int.TryParse(S(nameof(PresetDefaultsVersion),"1"),NumberStyles.Integer,CultureInfo.InvariantCulture,out var version)?version:1,
            Fps=S(nameof(Fps),"auto"),Device=S(nameof(Device),"auto"),DeviceName=S(nameof(DeviceName),""),Encoder=S(nameof(Encoder),"auto"),Model=S(nameof(Model),"v4.25"),
            Accelerator=S(nameof(Accelerator),"auto"),
            Verify=B(nameof(Verify),false),HardwareEncoding=B(nameof(HardwareEncoding),true),CheckUpdates=B(nameof(CheckUpdates),true),
            CloseAction=S(nameof(CloseAction),"ask"),MinimizeAction=S(nameof(MinimizeAction),"taskbar"),
            StartPage=S(nameof(StartPage),"capture"),CaptureDevice=S(nameof(CaptureDevice),""),CaptureMode=S(nameof(CaptureMode),""),CaptureAudio=S(nameof(CaptureAudio),"auto"),
            CaptureDeviceId=S(nameof(CaptureDeviceId),""),CaptureAudioOutput=S(nameof(CaptureAudioOutput),"default"),
            LiveDlssMotion=S(nameof(LiveDlssMotion),"none"),LiveEdgeAegis=B(nameof(LiveEdgeAegis),false),
            LiveFrameGeneration=S(nameof(LiveFrameGeneration),"off"),LiveMultiplier=I(nameof(LiveMultiplier),2),LiveAntiAlias=I(nameof(LiveAntiAlias),0),LiveDedup=B(nameof(LiveDedup),false),
            LiveRange=S(nameof(LiveRange),"auto"),LiveVfxQuality=I(nameof(LiveVfxQuality),1),LiveVolume=I(nameof(LiveVolume),100),LiveMute=B(nameof(LiveMute),false),
            LiveAudioOffset=I(nameof(LiveAudioOffset),0),LiveStats=B(nameof(LiveStats),true),LiveMatrix=S(nameof(LiveMatrix),"auto"),LiveSync=S(nameof(LiveSync),"auto"),
            LiveAspect=S(nameof(LiveAspect),"fit"),LiveOutputCap=S(nameof(LiveOutputCap),"off"),LiveHoldOriginal=B(nameof(LiveHoldOriginal),true),ShortcutOriginal=S(nameof(ShortcutOriginal),"V"),ScreenshotDirectory=S(nameof(ScreenshotDirectory),""),RecordingDirectory=S(nameof(RecordingDirectory),""),
            RecordingCodec=S(nameof(RecordingCodec),"h264"),RecordingBitrate=I(nameof(RecordingBitrate),0),
            ShortcutScreenshot=S(nameof(ShortcutScreenshot),"F7"),ShortcutRecord=S(nameof(ShortcutRecord),"F8"),ShortcutFullscreen=S(nameof(ShortcutFullscreen),"F11"),
            ShortcutFrameGeneration=S(nameof(ShortcutFrameGeneration),"F6"),ShortcutStats=S(nameof(ShortcutStats),"F10"),GlobalShortcuts=B(nameof(GlobalShortcuts),false)};
        return Normalize(result,values.ContainsKey(nameof(PresetDefaultsVersion)));
    }
    public void Save(string? directory=null){
        directory??=DirectoryPath;System.IO.Directory.CreateDirectory(directory);
        var content=new StringBuilder("; FrameDoubler portable settings (UTF-8)\n[FrameDoubler]\n");
        void Add(string name,object value){
            string text=Convert.ToString(value,CultureInfo.InvariantCulture)??"";
            if(text.Contains('\r')||text.Contains('\n'))throw new FormatException("INI settings must fit on one line");
            content.Append(name).Append("=\"").Append(text.Replace("\"","\"\"")).Append("\"\n");
        }
        Add(nameof(Language),Language);Add(nameof(OutputDirectory),OutputDirectory);Add(nameof(Preset),Preset);Add(nameof(PresetDefaultsVersion),PresetDefaultsVersion);
        Add(nameof(Fps),Fps);Add(nameof(Device),Device);Add(nameof(DeviceName),DeviceName);Add(nameof(Encoder),Encoder);Add(nameof(Model),Model);
        Add(nameof(Accelerator),Accelerator);
        Add(nameof(Verify),Verify);Add(nameof(HardwareEncoding),HardwareEncoding);Add(nameof(CheckUpdates),CheckUpdates);Add(nameof(CloseAction),CloseAction);Add(nameof(MinimizeAction),MinimizeAction);
        Add(nameof(StartPage),StartPage);Add(nameof(CaptureDevice),CaptureDevice);Add(nameof(CaptureMode),CaptureMode);Add(nameof(CaptureAudio),CaptureAudio);
        Add(nameof(CaptureAudioOutput),CaptureAudioOutput);
        Add(nameof(CaptureDeviceId),CaptureDeviceId);
        Add(nameof(LiveDlssMotion),LiveDlssMotion);Add(nameof(LiveEdgeAegis),LiveEdgeAegis);
        Add(nameof(LiveFrameGeneration),LiveFrameGeneration);Add(nameof(LiveMultiplier),LiveMultiplier);Add(nameof(LiveAntiAlias),LiveAntiAlias);Add(nameof(LiveDedup),LiveDedup);
        Add(nameof(LiveRange),LiveRange);Add(nameof(LiveVfxQuality),LiveVfxQuality);Add(nameof(LiveVolume),LiveVolume);Add(nameof(LiveMute),LiveMute);Add(nameof(LiveAudioOffset),LiveAudioOffset);
        Add(nameof(LiveStats),LiveStats);Add(nameof(LiveMatrix),LiveMatrix);Add(nameof(LiveSync),LiveSync);Add(nameof(LiveAspect),LiveAspect);
        Add(nameof(LiveOutputCap),LiveOutputCap);Add(nameof(LiveHoldOriginal),LiveHoldOriginal);Add(nameof(ShortcutOriginal),ShortcutOriginal);Add(nameof(ScreenshotDirectory),ScreenshotDirectory);Add(nameof(RecordingDirectory),RecordingDirectory);
        Add(nameof(RecordingCodec),RecordingCodec);Add(nameof(RecordingBitrate),RecordingBitrate);
        Add(nameof(ShortcutScreenshot),ShortcutScreenshot);Add(nameof(ShortcutRecord),ShortcutRecord);Add(nameof(ShortcutFullscreen),ShortcutFullscreen);
        Add(nameof(ShortcutFrameGeneration),ShortcutFrameGeneration);Add(nameof(ShortcutStats),ShortcutStats);Add(nameof(GlobalShortcuts),GlobalShortcuts);
        string target=Path.Combine(directory,FileName),temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,content.ToString(),new UTF8Encoding(false));File.Move(temp,target,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
