using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using FrameDoubler.Core;

namespace FrameDoubler.App;

// While the capture preview runs, Windows must not dim the display or sleep; the request is
// dropped when the preview stops or the program closes (SetThreadExecutionState on the UI thread).
internal static class KeepAwake
{
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private const uint Continuous=0x80000000,SystemRequired=0x1,DisplayRequired=0x2;
    private static bool active;
    public static void Set(bool on){
        if(on==active)return;
        active=on;
        SetThreadExecutionState(on?Continuous|SystemRequired|DisplayRequired:Continuous);
    }
}

// A manual report for support: nothing is sent anywhere. The ZIP holds the program and system
// versions, the capture status and devices, the settings and the recent log, with the user name,
// profile path and device instance IDs replaced. No picture or sound is included.
internal static class Diagnostics
{
    public static void Write(string zipPath,string log,string? captureStatus){
        string user=Environment.UserName,profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Mask(string text)=>Privacy.Mask(text,user,profile);
        var info=new StringBuilder();
        info.AppendLine("FrameDoubler "+typeof(Diagnostics).Assembly.GetName().Version);
        info.AppendLine("Created: "+DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        info.AppendLine("OS: "+Environment.OSVersion+" ("+RuntimeInformation.OSArchitecture+")");
        info.AppendLine(".NET: "+RuntimeInformation.FrameworkDescription);
        info.AppendLine("Processors: "+Environment.ProcessorCount);
        info.AppendLine("Live engine: "+(LiveEngine.Available?LiveEngine.LibraryPath:"missing"));
        info.AppendLine("DLSS runtime: "+(LiveEngine.DlssInstalled?"present":"absent")+(LiveEngine.DlssBlocked?" (blocked after an abnormal exit)":""));
        info.AppendLine("NVIDIA VFX runtime: "+(LiveEngine.VfxInstalled?"present":"absent"));
        string devices;
        try{devices=LiveEngine.Available?LiveEngine.DevicesJson():"live engine missing";}catch(Exception ex){devices="device list failed: "+ex.Message;}
        string settings=Path.Combine(Preferences.DirectoryPath,Preferences.FileName);
        if(File.Exists(zipPath))File.Delete(zipPath);
        using var zip=ZipFile.Open(zipPath,ZipArchiveMode.Create);
        void Add(string name,string text){using var w=new StreamWriter(zip.CreateEntry(name,CompressionLevel.Optimal).Open(),new UTF8Encoding(false));w.Write(Mask(text));}
        Add("info.txt",info.ToString());
        Add("capture-status.json",captureStatus??"preview not running");
        Add("devices.json",devices);
        Add("settings.ini",File.Exists(settings)?File.ReadAllText(settings):"none");
        string[] lines=log.Replace("\r\n","\n").Split('\n');
        Add("log.txt",string.Join(Environment.NewLine,lines[Math.Max(0,lines.Length-2000)..]));
    }
}
