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
    public string DeviceName{get;init;}="";
    public string Encoder{get;init;}="auto";
    public string Model{get;init;}="v4.25";
    public bool Verify{get;init;}=false;
    public bool HardwareEncoding{get;init;}=true;
    public bool CheckUpdates{get;init;}=true;
    public string CloseAction{get;init;}="ask";
    public string MinimizeAction{get;init;}="taskbar";
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
        bool B(string key,bool fallback)=>values.TryGetValue(key,out var value)?value.ToLowerInvariant() switch{"true" or "1" or "yes"=>true,"false" or "0" or "no"=>false,_=>fallback}:fallback;
        var result=new Preferences{
            Language=S(nameof(Language),"ko"),OutputDirectory=S(nameof(OutputDirectory),""),Preset=S(nameof(Preset),"source"),
            PresetDefaultsVersion=int.TryParse(S(nameof(PresetDefaultsVersion),"1"),NumberStyles.Integer,CultureInfo.InvariantCulture,out var version)?version:1,
            Fps=S(nameof(Fps),"auto"),Device=S(nameof(Device),"auto"),DeviceName=S(nameof(DeviceName),""),Encoder=S(nameof(Encoder),"auto"),Model=S(nameof(Model),"v4.25"),
            Verify=B(nameof(Verify),false),HardwareEncoding=B(nameof(HardwareEncoding),true),CheckUpdates=B(nameof(CheckUpdates),true),
            CloseAction=S(nameof(CloseAction),"ask"),MinimizeAction=S(nameof(MinimizeAction),"taskbar")};
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
        Add(nameof(Verify),Verify);Add(nameof(HardwareEncoding),HardwareEncoding);Add(nameof(CheckUpdates),CheckUpdates);Add(nameof(CloseAction),CloseAction);Add(nameof(MinimizeAction),MinimizeAction);
        string target=Path.Combine(directory,FileName),temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,content.ToString(),new UTF8Encoding(false));File.Move(temp,target,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
