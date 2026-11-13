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
    public bool Verify{get;init;}=true;
    public bool HardwareEncoding{get;init;}=true;
    public static string DirectoryPath=>Environment.GetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR")
        ??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"FrameDoubler");
    public static Preferences Load(){
        try{
            string file=Path.Combine(DirectoryPath,"native-settings.json");
            if(File.Exists(file))return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(file))??new();
            string legacy=Path.Combine(DirectoryPath,"settings.json");
            if(File.Exists(legacy)){
                using var doc=JsonDocument.Parse(File.ReadAllText(legacy));
                string Value(string name,string fallback)=>doc.RootElement.TryGetProperty(name,out var v)?v.GetString()??fallback:fallback;
                return new(){Language=Value("language","ko"),OutputDirectory=Value("output_dir",""),Preset=Value("preset","youtube")};
            }
        }catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){}
        return new();
    }
    public void Save(){
        System.IO.Directory.CreateDirectory(DirectoryPath);
        string target=Path.Combine(DirectoryPath,"native-settings.json"),temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,target,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
