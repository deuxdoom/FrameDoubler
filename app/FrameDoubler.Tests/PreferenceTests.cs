using FrameDoubler.Core;
using System.Text;

// settings.ini next to the exe (1.0.2+), with a one-time migration of the earlier settings.json.
internal static class PreferenceTests
{
    public static void Run(Action<bool,string> assert){
        string? previous=Environment.GetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR");
        string root=Path.Combine(EncoderDetector.WorkRoot,"settings-tests",Guid.NewGuid().ToString("N"));
        string Fresh(string name){string path=Path.Combine(root,name);Directory.CreateDirectory(path);return path;}
        try{
            string config=Fresh("round trip");
            Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",config);
            string tricky="F:\\한글 출력\\a=b; \"따옴표\" #해시";
            new Preferences{Language="en",OutputDirectory=tricky,Preset="source-scaled",Fps="60000/1001",DeviceName="GPU \"0\" = RTX; # test",Verify=true,HardwareEncoding=false,CheckUpdates=false,CloseAction="tray",MinimizeAction="tray"}.Save();
            string ini=Path.Combine(config,Preferences.FileName);
            byte[] bytes=File.ReadAllBytes(ini);
            assert(bytes.Length>3&&!(bytes[0]==0xEF&&bytes[1]==0xBB&&bytes[2]==0xBF)&&Encoding.UTF8.GetString(bytes).Contains("[FrameDoubler]")&&!File.Exists(Path.Combine(config,"settings.json")),
                "Settings are written as UTF-8 settings.ini with a [FrameDoubler] section");
            var loaded=Preferences.Load();
            assert(loaded.Language=="en"&&loaded.OutputDirectory==tricky&&loaded.Preset=="source-scaled"&&loaded.Fps=="60000/1001"&&loaded.DeviceName=="GPU \"0\" = RTX; # test",
                "Restart keeps Unicode, spaces, equals signs, quotes, ';' and '#' exactly");
            assert(loaded.Verify&&!loaded.HardwareEncoding&&!loaded.CheckUpdates&&loaded.CloseAction=="tray"&&loaded.MinimizeAction=="tray","Booleans and window actions survive a restart");
            bool multiline=false;try{new Preferences{OutputDirectory="a\nb"}.Save();}catch(FormatException){multiline=true;}
            assert(multiline&&Preferences.Load().OutputDirectory==tricky,"A value that cannot fit on one line is refused without touching the saved file");

            // Hand-edited file: BOM, CRLF, comments, other sections, unquoted values and boolean spellings.
            File.WriteAllText(ini,"; edited\r\n[Other]\r\nLanguage=en\r\n\r\n[FrameDoubler]\r\n# comment\r\nLanguage = en \r\nVerify=1\r\nHardwareEncoding=no\r\nCheckUpdates=FALSE\r\nUnknown=ignored\r\nPreset=\"source-scaled\"\r\n",new UTF8Encoding(true));
            loaded=Preferences.Load();
            assert(loaded.Language=="en"&&loaded.Verify&&!loaded.HardwareEncoding&&!loaded.CheckUpdates&&loaded.Preset=="source-scaled"&&loaded.Model=="v4.25",
                "Hand-edited INI (BOM, CRLF, comments, other sections, 1/no/FALSE) reads only the [FrameDoubler] section; missing keys use defaults");
            File.WriteAllText(ini,"[FrameDoubler]\nLanguage=\"en\nVerify=true\n");
            assert(Preferences.Load() is {Language:"ko",Verify:false},"A broken INI value falls back to defaults instead of failing to start");

            // Normalization of removed or implicit values, read through the INI.
            foreach(string removed in new[]{"youtube-2160","youtube-1080","youtube-1440","youtube"}){
                new Preferences{Preset=removed}.Save();assert(Preferences.Load().Preset=="source","Removed YouTube preset falls back to the source preset: "+removed);
            }
            new Preferences{Preset="quality"}.Save();assert(Preferences.Load().Preset=="source","Unknown preset falls back to the source preset");
            new Preferences{Preset="source-scaled"}.Save();assert(Preferences.Load().Preset=="source-scaled","Explicit preset selection persists");
            assert(new Preferences().MinimizeAction=="taskbar","Minimize defaults to the taskbar");
            File.WriteAllText(ini,"[FrameDoubler]\nLanguage=\"ko\"\n");
            assert(Preferences.Load() is {StartPage:"convert",ShortcutRemove:"Delete",ShortcutAddVideos:"Ctrl+O",ShortcutConvertAll:"Ctrl+Enter",ShortcutOpenResult:"Enter"},
                "New installs start on file conversion with the default conversion shortcuts");
            new Preferences{StartPage="capture",ShortcutRemove="",ShortcutMoveUp="Ctrl+Shift+Up",ShortcutConvertSelected="F9"}.Save();
            assert(Preferences.Load() is {StartPage:"capture",ShortcutRemove:"",ShortcutMoveUp:"Ctrl+Shift+Up",ShortcutConvertSelected:"F9",ShortcutSelectAll:"Ctrl+A"},
                "The last page and changed or cleared conversion shortcuts survive a restart");
            new Preferences{MinimizeAction="ask"}.Save();assert(Preferences.Load().MinimizeAction=="taskbar","Previous implicit ask behavior becomes taskbar minimize");

            // One-time migration of settings.json (1.0.0/1.0.1).
            config=Fresh("legacy 한글");
            Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",config);
            string legacy=Path.Combine(config,"settings.json");
            File.WriteAllText(legacy,"{\"Language\":\"en\",\"OutputDirectory\":\"F:\\\\변환 결과\",\"Preset\":\"youtube\",\"CloseAction\":\"exit\",\"MinimizeAction\":\"ask\"}");
            loaded=Preferences.Load();
            assert(loaded.Language=="en"&&loaded.OutputDirectory=="F:\\변환 결과"&&loaded.Preset=="source"&&loaded.CloseAction=="exit"&&loaded.MinimizeAction=="taskbar","Legacy settings.json values are migrated and normalized");
            assert(File.Exists(Path.Combine(config,Preferences.FileName))&&!File.Exists(legacy)&&Preferences.Load().OutputDirectory=="F:\\변환 결과","Migration writes settings.ini first, then removes settings.json");

            config=Fresh("priority");
            Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",config);
            new Preferences{Language="en"}.Save();
            File.WriteAllText(Path.Combine(config,"settings.json"),"{\"Language\":\"ko\",\"Verify\":true}");
            assert(Preferences.Load() is {Language:"en",Verify:false}&&File.Exists(Path.Combine(config,"settings.json")),"settings.ini wins over a leftover settings.json, which is left untouched");

            config=Fresh("malformed json");
            Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",config);
            File.WriteAllText(Path.Combine(config,"settings.json"),"invalid json");
            assert(Preferences.Load().Language=="ko"&&File.Exists(Path.Combine(config,"settings.json"))&&!File.Exists(Path.Combine(config,Preferences.FileName)),"Malformed legacy settings recover safely and are not deleted");

            config=Fresh("save failure");
            Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",config);
            File.WriteAllText(Path.Combine(config,"settings.json"),"{\"Language\":\"en\",\"PresetDefaultsVersion\":1,\"Preset\":\"source-scaled\"}");
            Directory.CreateDirectory(Path.Combine(config,Preferences.FileName));   // the INI cannot be written here
            loaded=Preferences.Load();
            assert(loaded.Language=="en"&&loaded.Preset=="source-scaled"&&File.Exists(Path.Combine(config,"settings.json")),"If settings.ini cannot be written, settings.json is kept and still used");
        }finally{Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",previous);}

        // Update preservation: settings.ini and performance.json move to the new version; settings.json only when never migrated.
        string install=Path.Combine(root,"FrameDoubler"),stage=Path.Combine(root,".FrameDoubler-update-preserve");
        Directory.CreateDirectory(install);Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(install,Preferences.FileName),"[FrameDoubler]\nLanguage=\"en\"\n");File.WriteAllText(Path.Combine(install,"settings.json"),"stale");
        File.WriteAllText(Path.Combine(install,"performance.json"),"speeds");File.WriteAllText(Path.Combine(install,"untrusted.exe"),"skip");
        TimeEstimates.PreservePortableFiles(install,stage);
        assert(File.ReadAllText(Path.Combine(stage,Preferences.FileName)).Contains("Language=\"en\"")&&File.ReadAllText(Path.Combine(stage,"performance.json"))=="speeds"
            &&!File.Exists(Path.Combine(stage,"settings.json"))&&!File.Exists(Path.Combine(stage,"untrusted.exe")),"Update carries settings.ini and performance.json, not a stale settings.json or executables");
        string oldInstall=Path.Combine(root,"Old FrameDoubler"),oldStage=Path.Combine(root,".FrameDoubler-update-legacy");
        Directory.CreateDirectory(oldInstall);Directory.CreateDirectory(oldStage);File.WriteAllText(Path.Combine(oldInstall,"settings.json"),"settings");
        TimeEstimates.PreservePortableFiles(oldInstall,oldStage);
        assert(File.ReadAllText(Path.Combine(oldStage,"settings.json"))=="settings","An install that never migrated carries settings.json for the new version to convert");
        bool unsafeStage=false;try{TimeEstimates.PreservePortableFiles(install,Path.Combine(root,"elsewhere"));}catch(IOException){unsafeStage=true;}
        assert(unsafeStage,"Portable files are copied only into an update stage beside the installation");
    }
}
