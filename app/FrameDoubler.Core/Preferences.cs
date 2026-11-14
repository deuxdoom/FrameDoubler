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
    // Close: ask | tray | exit. Minimize: taskbar (default) | tray (explicit choice).
    public string CloseAction{get;init;}="ask";
    public string MinimizeAction{get;init;}="taskbar";
    public static string DirectoryPath=>Environment.GetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR")
        ??AppContext.BaseDirectory;
    public static Preferences Load(){
        try{
            string file=Path.Combine(DirectoryPath,"settings.json");
            if(File.Exists(file)){
                string text=File.ReadAllText(file);var value=JsonSerializer.Deserialize<Preferences>(text)??new();
                using var document=JsonDocument.Parse(text);
                // The original default was YouTube. Move that old default once;
                // subsequent explicit platform choices remain the user's choice.
                if(!document.RootElement.TryGetProperty(nameof(PresetDefaultsVersion),out _)&&value.Preset=="youtube")value=value with{Preset="source"};
                if(value.Preset is "youtube" or "youtube-1440")value=value with{Preset="youtube-2160"};
                if(OutputPresets.FindExact(value.Preset) is null)value=value with{Preset="source"};
                if(value.MinimizeAction!="tray")value=value with{MinimizeAction="taskbar"};
                return value;
            }
        }catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){}
        return new();
    }
    public void Save(){
        System.IO.Directory.CreateDirectory(DirectoryPath);
        string target=Path.Combine(DirectoryPath,"settings.json"),temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,target,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
