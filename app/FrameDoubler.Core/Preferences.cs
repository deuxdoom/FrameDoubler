using System.Text.Json;

namespace FrameDoubler.Core;

public sealed record Preferences
{
    public string Language{get;init;}="ko";
    public string OutputDirectory{get;init;}="";
    public string Preset{get;init;}="youtube";
    public string Fps{get;init;}="auto";
    public string Codec{get;init;}="auto";
    public string Device{get;init;}="auto";
    public string DeviceName{get;init;}="";
    public string Encoder{get;init;}="auto";
    public string Model{get;init;}="v4.25";
    public int Quality{get;init;}=19;
    public bool Verify{get;init;}=false;
    public bool HardwareEncoding{get;init;}=true;
    public bool CheckUpdates{get;init;}=true;
    // "ask" | "tray" | "exit" for the close button; "ask" | "tray" | "taskbar" for minimize.
    public string CloseAction{get;init;}="ask";
    public string MinimizeAction{get;init;}="ask";
    public static string DirectoryPath=>Environment.GetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR")
        ??AppContext.BaseDirectory;
    public static Preferences Load(){
        try{
            string file=Path.Combine(DirectoryPath,"settings.json");
            if(File.Exists(file))return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(file))??new();
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
