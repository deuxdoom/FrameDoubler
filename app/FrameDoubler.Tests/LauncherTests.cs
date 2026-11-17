using FrameDoubler.Core;
using System.Diagnostics;
using System.Text.Json;

// "--launcher-package <dist folder> <versioned zip>": the real package in a Hangul/space path, run without
// PATH or a global .NET, then the real GUI update flow (button → exit → installer window → restart).
internal static class LauncherTests
{
    private static void Copy(string source,string destination){
        foreach(string directory in Directory.EnumerateDirectories(source,"*",SearchOption.AllDirectories))Directory.CreateDirectory(Path.Combine(destination,Path.GetRelativePath(source,directory)));
        Directory.CreateDirectory(destination);
        foreach(string file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))File.Copy(file,Path.Combine(destination,Path.GetRelativePath(source,file)));
    }
    private static ProcessStartInfo Info(string executable,string working,params string[] arguments){
        var info=ProcessRunner.StartInfo(executable,arguments);info.WorkingDirectory=working;
        // No PATH entries beyond Windows itself and no global .NET: the launcher must use dll/ only.
        info.Environment["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach(string name in new[]{"FRAMEDOUBLER_WORK_ROOT","FRAMEDOUBLER_CONFIG_DIR","FRAMEDOUBLER_DATA","DOTNET_ROOT(x86)","DOTNET_ROOT_X64"})info.Environment.Remove(name);
        info.Environment["DOTNET_ROOT"]=Path.Combine(working,"no-dotnet-here");info.Environment["DOTNET_MULTILEVEL_LOOKUP"]="0";
        return info;
    }
    private static async Task<ProcessResult> Run(ProcessStartInfo info,double seconds){
        using var process=ProcessRunner.Start(info,Path.GetFileName(info.FileName));
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try{await process.WaitForExitAsync(limit.Token);}catch(OperationCanceledException){process.Kill(true);throw new TimeoutException(Path.GetFileName(info.FileName)+" timed out");}
        return new(process.ExitCode,await output,await error);
    }
    private static async Task<bool> Until(Func<bool> condition,double seconds){
        var watch=Stopwatch.StartNew();while(watch.Elapsed.TotalSeconds<seconds){if(condition())return true;await Task.Delay(200);}return condition();
    }
    public static async Task RunAsync(string package,string archive,Action<bool,string> assert){
        package=Path.GetFullPath(package);archive=Path.GetFullPath(archive);
        string root=Path.Combine(EncoderDetector.WorkRoot,"launcher-tests",Guid.NewGuid().ToString("N"),"한글 공백 폴더");
        string app=Path.Combine(root,"Frame Doubler");Copy(package,app);
        assert(File.Exists(Path.Combine(app,"FrameDoubler.exe"))&&File.Exists(Path.Combine(app,"FrameDoubler.Cli.exe"))&&File.Exists(Path.Combine(app,"dll","FrameDoubler.dll"))
            &&File.Exists(Path.Combine(app,"dll","FrameDoubler.Cli.dll"))&&File.Exists(Path.Combine(app,"dll","hostfxr.dll"))&&File.Exists(Path.Combine(app,"dll","coreclr.dll"))
            &&!Directory.EnumerateFiles(app,"*.dll").Any()&&!File.Exists(Path.Combine(app,"dll","FrameDoubler.exe"))&&!File.Exists(Path.Combine(app,"dll","FrameDoubler.Cli.exe")),
            "Package root has only the two launchers; the .NET runtime and app assemblies are in dll/");
        foreach(string name in new[]{"FrameDoubler.exe","FrameDoubler.Cli.exe"}){
            var version=FileVersionInfo.GetVersionInfo(Path.Combine(app,name));
            assert(version.FileVersion==Updates.Current.ToString(3)+".0"&&version.FileDescription==Path.GetFileNameWithoutExtension(name),"Launcher carries the app version and name: "+name);
        }

        // CLI from a Hangul/space path, without PATH or a global .NET.
        string cli=Path.Combine(app,"FrameDoubler.Cli.exe");
        var usage=await Run(Info(cli,root),60);
        assert(usage.ExitCode==0&&usage.Output.Contains("FrameDoubler: hardware",StringComparison.Ordinal),"CLI launcher starts the bundled runtime: "+usage.Error);
        var hardware=await Run(Info(cli,root,"hardware"),180);
        using(var found=JsonDocument.Parse(hardware.Output))
            assert(hardware.ExitCode==0&&found.RootElement.GetProperty("Encoders").EnumerateArray().Any(x=>x.GetProperty("Available").GetBoolean()),"CLI finds FFmpeg and the engine next to the launcher: "+hardware.Error);
        string clip=Path.Combine(root,"입력 영상.mp4"),output=Path.Combine(root,"출력 결과.mp4");
        var made=await Run(Info(Path.Combine(app,"ffmpeg","ffmpeg.exe"),root,"-v","error","-f","lavfi","-i","testsrc=size=320x180:rate=30","-t","1","-pix_fmt","yuv420p","-c:v","libopenh264",clip),60);
        assert(made.ExitCode==0&&File.Exists(clip),"Test clip "+made.Error);
        var converted=await Run(Info(cli,root,"run","-i",clip,"-o",output,"--no-verify","--quiet"),300);
        assert(converted.ExitCode==0&&File.Exists(output),"CLI converts from the dll layout: "+converted.Error);
        assert(Directory.Exists(Path.Combine(app,"work"))&&!Directory.Exists(Path.Combine(app,"dll","work"))&&!File.Exists(Path.Combine(app,"dll",Preferences.FileName)),
            "Work files are created next to the launcher, never inside dll/");

        // GUI diagnostics from the same folder; the runtime that loads is the bundled one.
        string report=Path.Combine(root,"ui","report.json");
        var ui=Info(Path.Combine(app,"FrameDoubler.exe"),root,"--ui-test",report);
        using(var gui=ProcessRunner.Start(ui,"GUI ui-test")){
            _=gui.StandardOutput.ReadToEndAsync();_=gui.StandardError.ReadToEndAsync();
            string? runtime=null;
            await Until(()=>{try{gui.Refresh();runtime=gui.Modules.Cast<ProcessModule>().FirstOrDefault(x=>x.ModuleName.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase))?.FileName;}catch(Exception ex)when(ex is InvalidOperationException or System.ComponentModel.Win32Exception){}return runtime is not null||gui.HasExited;},60);
            assert(runtime is not null&&Path.GetDirectoryName(runtime)!.Equals(Path.Combine(app,"dll"),StringComparison.OrdinalIgnoreCase),"GUI runs on the bundled runtime in dll/: "+runtime);
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(300));await gui.WaitForExitAsync(limit.Token);
            assert(gui.ExitCode==0&&File.Exists(report),"GUI launcher passes the UI diagnostics: "+(File.Exists(report+".error.txt")?File.ReadAllText(report+".error.txt"):gui.ExitCode.ToString()));
        }

        // Missing pieces of dll/ give a clear message instead of a .NET install prompt.
        string blockedReport=Path.Combine(root,"blocked-report-directory");File.WriteAllText(blockedReport,"This is a file, not a directory.");
        var diagnosticFailure=await Run(Info(Path.Combine(app,"FrameDoubler.exe"),root,"--ui-test",Path.Combine(blockedReport,"report.json")),60);
        assert(diagnosticFailure.ExitCode==1&&diagnosticFailure.Error.Contains("Could not write diagnostic report",StringComparison.Ordinal),
            "An unwritable diagnostic report returns failure without a second unhandled exception: "+diagnosticFailure.Error);

        foreach(string missing in new[]{"hostfxr.dll","FrameDoubler.Cli.dll"}){
            string broken=Path.Combine(root,"broken "+missing);Directory.CreateDirectory(Path.Combine(broken,"dll"));
            File.Copy(cli,Path.Combine(broken,"FrameDoubler.Cli.exe"));
            foreach(string file in Directory.EnumerateFiles(Path.Combine(app,"dll"),"*.dll"))if(!Path.GetFileName(file).Equals(missing,StringComparison.OrdinalIgnoreCase))File.Copy(file,Path.Combine(broken,"dll",Path.GetFileName(file)));
            var failed=await Run(Info(Path.Combine(broken,"FrameDoubler.Cli.exe"),root),30);
            assert(failed.ExitCode==1&&failed.Error.Contains("dll folder is missing or incomplete",StringComparison.Ordinal),"Missing "+missing+" is reported clearly: "+failed.Error);
        }

        // Real GUI update flow from the same package: "Install update" → app exits → installer window
        // replaces the folder → new version restarts and confirms → settings and history carried over.
        string install=Path.Combine(root,"설치 위치","FrameDoubler");Copy(package,install);
        File.WriteAllText(Path.Combine(install,"old-marker.txt"),"previous installation");
        new Preferences{Language="en",OutputDirectory=Path.Combine(root,"결과 폴더"),Preset="source-scaled",CloseAction="tray"}.Save(install);
        File.WriteAllText(Path.Combine(install,"performance.json"),"[]");
        byte[] performance=File.ReadAllBytes(Path.Combine(install,"performance.json"));
        string updateReport=Path.Combine(root,"update-test.txt");
        var start=Info(Path.Combine(install,"FrameDoubler.exe"),root,"--update-test",updateReport,archive);
        start.Environment["FRAMEDOUBLER_INSTANCE_TEST_SCOPE"]="launcher-"+Guid.NewGuid().ToString("N");
        var watch=Stopwatch.StartNew();
        using(var old=ProcessRunner.Start(start,"GUI update test")){
            _=old.StandardOutput.ReadToEndAsync();_=old.StandardError.ReadToEndAsync();
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(300));await old.WaitForExitAsync(limit.Token);
            assert(!File.Exists(updateReport),"Update window completed the handoff: "+(File.Exists(updateReport)?File.ReadAllText(updateReport):""));
        }
        string last=Path.Combine(install,"work","updates","last-install.json");
        assert(await Until(()=>File.Exists(last),240),"Installer finished and recorded the result");
        using(var result=JsonDocument.Parse(File.ReadAllText(last)))
            assert(result.RootElement.GetProperty("Success").GetBoolean()&&result.RootElement.GetProperty("Phase").GetString()=="done","GUI update flow succeeds: "+File.ReadAllText(last));
        Console.WriteLine($"GUI update flow: {watch.Elapsed.TotalSeconds:0.0}s");
        string parent=Path.GetDirectoryName(install)!;
        string workspace=Directory.EnumerateDirectories(parent,".FrameDoubler-install-*").Single();
        string backup=Directory.EnumerateDirectories(parent,"FrameDoubler.previous-*").Single();
        assert(!File.Exists(Path.Combine(install,"old-marker.txt"))&&File.Exists(Path.Combine(backup,"old-marker.txt"))&&!Directory.EnumerateDirectories(parent,".FrameDoubler-update-*").Any(),
            "The installation folder was replaced and the previous one kept as a backup");
        var carried=Preferences.Load(install);
        assert(carried.Language=="en"&&carried.OutputDirectory==Path.Combine(root,"결과 폴더")&&carried.Preset=="source-scaled"&&carried.CloseAction=="tray"
            &&File.ReadAllBytes(Path.Combine(install,"performance.json")).SequenceEqual(performance),"settings.ini and performance.json carried over to the new version");
        int pid;using(var booted=JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace,"booted.json"))))pid=booted.RootElement.GetProperty("Pid").GetInt32();
        using(var restarted=Process.GetProcessById(pid)){
            assert(restarted.MainModule?.FileName.Equals(Path.Combine(install,"FrameDoubler.exe"),StringComparison.OrdinalIgnoreCase)==true,"The new version was restarted from the installation folder and confirmed startup");
            await Until(()=>!Process.GetProcessesByName("FrameDoubler").Any(x=>{try{return x.MainModule?.FileName.StartsWith(workspace,StringComparison.OrdinalIgnoreCase)==true;}catch(Exception){return false;}}),30);
            restarted.Kill(true);restarted.WaitForExit(10000);
        }
        await Task.Delay(1000);
        UpdateInstaller.CleanupFinished(install);
        assert(!Directory.Exists(workspace)&&Directory.Exists(backup),"Next start removes the installer copy and keeps the newest backup");
        Console.WriteLine("Launcher artifacts: "+root);
    }
}
