using FrameDoubler.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

// Real processes, real directory moves: the installer core (UpdateInstaller.Apply) against a fake app.
// A copy of this test program is installed as FrameDoubler.exe. Started by the installer with
// "--updated" it confirms startup; started without arguments it records that the old version reopened.
// The GUI parts (update window, installer window, restart) are checked separately with the real package.
internal static class InstallerTests
{
    private const string FakeVariable="FRAMEDOUBLER_FAKE_APP",BootVariable="FRAMEDOUBLER_FAKE_BOOT";
    public static bool FakeApp(string[] args){
        string? markers=Environment.GetEnvironmentVariable(FakeVariable);
        if(string.IsNullOrEmpty(markers))return false;
        if(args.Length>=1&&args[0]=="--fake-parent"){Thread.Sleep(TimeSpan.FromMinutes(3));return true;}
        if(args.Length>=2&&args[0]=="--updated"){
            if(Environment.GetEnvironmentVariable(BootVariable)!="exit"){UpdateInstaller.AcknowledgeStartup(args[1]);Thread.Sleep(3000);}
            return true;
        }
        Directory.CreateDirectory(markers);
        File.WriteAllText(Path.Combine(markers,$"restarted-{Environment.ProcessId}.txt"),Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        return true;
    }
    private static readonly string[] ProgramFiles=["FrameDoubler.Tests.dll","FrameDoubler.Tests.runtimeconfig.json","FrameDoubler.Tests.deps.json","FrameDoubler.Core.dll"];
    private static void FakeInstall(string folder,string label){
        Directory.CreateDirectory(folder);string bin=AppContext.BaseDirectory;
        File.Copy(Path.Combine(bin,"FrameDoubler.Tests.exe"),Path.Combine(folder,"FrameDoubler.exe"));
        foreach(string name in ProgramFiles)File.Copy(Path.Combine(bin,name),Path.Combine(folder,name));
        Directory.CreateDirectory(Path.Combine(folder,"engine"));Directory.CreateDirectory(Path.Combine(folder,"dll"));
        File.WriteAllText(Path.Combine(folder,"engine","rife-poc.exe"),"engine "+label);
        File.WriteAllText(Path.Combine(folder,"dll","FrameDoubler.dll"),"app "+label);
        File.WriteAllText(Path.Combine(folder,"dll","hostfxr.dll"),"host "+label);
        File.WriteAllText(Path.Combine(folder,"version.txt"),label);
    }
    private static string FakeStage(string parent,string label){
        string stage=Path.Combine(parent,".FrameDoubler-update-"+label);FakeInstall(stage,label);
        var files=Directory.EnumerateFiles(stage,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(x=>new{path=Path.GetRelativePath(stage,x).Replace('\\','/'),bytes=new FileInfo(x).Length,sha256=UpdateInstaller.Hash(x).ToLowerInvariant()}).ToArray();
        File.WriteAllText(Path.Combine(stage,"manifest.json"),JsonSerializer.Serialize(new{format="framedoubler-native-v1",version=Updates.Current.ToString(3),runtime="win-x64 self-contained .NET 10",files}));
        return stage;
    }
    private sealed class Recorder:IProgress<InstallProgress>{public readonly ConcurrentQueue<InstallProgress> Items=new();public void Report(InstallProgress value)=>Items.Enqueue(value);}
    private sealed record Run(string Target,string Stage,string Workspace,string Request,InstallRequest Value,Process Parent,Task<InstallResult> Apply,Recorder Events){
        public string[] Phases=>Events.Items.Select(x=>x.Phase).Where(x=>x!="retry").ToArray();
        public string State{get{try{using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(Workspace,"state.json")));return json.RootElement.GetProperty("Phase").GetString()??"";}catch(Exception ex)when(ex is IOException or JsonException){return "";}}}
        public bool Has(string name)=>File.Exists(Path.Combine(Workspace,name));
        public void Signal(string name)=>UpdateInstaller.WriteJson(Path.Combine(Workspace,name),new{Value.Token});
        public string Version=>File.ReadAllText(Path.Combine(Target,"version.txt"));
    }
    private static string parentApp="";
    private static Run Start(string parent,string label,string? target=null,Action<string>? alterStage=null,Func<InstallRequest,InstallRequest>? alterRequest=null){
        Directory.CreateDirectory(parent);
        target??=Path.Combine(parent,"Frame Doubler 한글");
        if(!Directory.Exists(target)){
            FakeInstall(target,"old");
            File.WriteAllText(Path.Combine(target,Preferences.FileName),"[FrameDoubler]\nOutputDirectory=\"F:\\한글 출력\"\n",new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(target,"performance.json"),"{\"profiles\":[1]}");
        }
        string stage=FakeStage(parent,label);alterStage?.Invoke(stage);
        string workspace=Path.Combine(parent,".FrameDoubler-install-"+label),runner=Path.Combine(workspace,"runner");Directory.CreateDirectory(runner);
        var process=ProcessRunner.Start(ProcessRunner.StartInfo(Path.Combine(parentApp,"FrameDoubler.exe"),["--fake-parent"]),"fake app");
        var request=new InstallRequest(target,stage,workspace,Guid.NewGuid().ToString("N"),process.Id,process.StartTime.ToUniversalTime().Ticks,Updates.Current.ToString(3),
            UpdateInstaller.Hash(Path.Combine(stage,"manifest.json")),false);
        if(alterRequest is not null)request=alterRequest(request);
        string file=Path.Combine(workspace,UpdateInstaller.RequestName);UpdateInstaller.WriteJson(file,request);
        var events=new Recorder();
        return new(target,stage,workspace,file,request,process,Task.Run(()=>UpdateInstaller.Apply(file,runner,events)),events);
    }
    private static async Task<bool> Until(Func<bool> condition,double seconds){
        var watch=Stopwatch.StartNew();while(watch.Elapsed.TotalSeconds<seconds){if(condition())return true;await Task.Delay(50);}return condition();
    }
    private static void Stop(Process process){try{if(!process.HasExited)process.Kill(true);process.WaitForExit(5000);}catch(InvalidOperationException){}}
    private static bool Backups(string target)=>Directory.EnumerateDirectories(Path.GetDirectoryName(target)!,Path.GetFileName(target)+".previous-*").Any();

    public static async Task RunAsync(Action<bool,string> assert){
        string root=Path.Combine(EncoderDetector.WorkRoot,"installer-tests",Guid.NewGuid().ToString("N"));
        string markers=Path.Combine(root,"markers");Directory.CreateDirectory(markers);
        string[] Restarts()=>Directory.EnumerateFiles(markers).Select(File.ReadAllText).ToArray();
        string? previousFake=Environment.GetEnvironmentVariable(FakeVariable),previousBoot=Environment.GetEnvironmentVariable(BootVariable);
        Environment.SetEnvironmentVariable(FakeVariable,markers);Environment.SetEnvironmentVariable(BootVariable,null);
        parentApp=Path.Combine(root,"parent app");FakeInstall(parentApp,"parent");
        try{
            // 1. Normal update: ready while the app is open, nothing moved before it exits, a short lock is retried.
            string shared=Path.Combine(root,"공유 폴더");
            var a=Start(shared,"first");
            assert(await Until(()=>a.Has("ready.json")||a.Apply.IsCompleted,30)&&!a.Apply.IsCompleted,"Installer verifies the payload and reports ready while the app is still open");
            TimeEstimates.PreservePortableFiles(a.Target,a.Stage);   // what the update window does after ready
            byte[] settings=File.ReadAllBytes(Path.Combine(a.Target,Preferences.FileName)),speeds=File.ReadAllBytes(Path.Combine(a.Target,"performance.json"));
            a.Signal("commit.json");
            await Task.Delay(1000);
            assert(a.State=="waiting"&&a.Version=="old"&&!Backups(a.Target),"Nothing is replaced while the original app process is still running");
            var held=new FileStream(Path.Combine(a.Target,"version.txt"),FileMode.Open,FileAccess.Read,FileShare.Read);
            Stop(a.Parent);await Task.Delay(2500);held.Dispose();
            var first=await a.Apply.WaitAsync(TimeSpan.FromSeconds(120));
            assert(first.Success&&a.Version=="first"&&File.ReadAllText(Path.Combine(first.Backup,"version.txt"))=="old","Update installs after the app exits and keeps the previous version as a backup");
            assert(a.Events.Items.Any(x=>x.Phase=="retry"),"A file still locked after the app exits is retried instead of failing the update");
            assert(a.Phases.SequenceEqual(new[]{"verify","handoff","waiting","backup","install","starting","done"}),"Installer phases run in order: "+string.Join(",",a.Phases));
            assert(File.ReadAllBytes(Path.Combine(a.Target,Preferences.FileName)).SequenceEqual(settings)&&File.ReadAllBytes(Path.Combine(a.Target,"performance.json")).SequenceEqual(speeds),"settings.ini and performance.json are carried over byte for byte");
            assert(UpdateInstaller.ReadResult(a.Workspace)?.Success==true&&File.Exists(Path.Combine(a.Target,"work","updates","last-install.json"))&&!Directory.Exists(a.Stage)&&Restarts().Length==0,
                "Result is recorded in the workspace and the new installation; the old version is not reopened");

            // 2. A second update over the first (PID/start time of a new app), then housekeeping keeps one backup.
            await Task.Delay(3500);
            var b=Start(shared,"second",a.Target);
            assert(await Until(()=>b.Has("ready.json")||b.Apply.IsCompleted,30),"Repeat update gets ready");
            b.Signal("commit.json");Stop(b.Parent);
            var second=await b.Apply.WaitAsync(TimeSpan.FromSeconds(120));
            assert(second.Success&&b.Version=="second","Repeated updates of the same installation succeed");
            await Task.Delay(3500);
            UpdateInstaller.CleanupFinished(b.Target);
            var left=Directory.EnumerateDirectories(shared,Path.GetFileName(b.Target)+".previous-*").ToArray();
            assert(left.Length==1&&File.ReadAllText(Path.Combine(left[0],"version.txt"))=="first"&&!Directory.Exists(a.Workspace)&&!Directory.Exists(b.Workspace),
                "Startup housekeeping removes finished installer folders and keeps only the newest backup");

            // 3. The new version does not confirm startup: roll back and reopen the previous version.
            Environment.SetEnvironmentVariable(BootVariable,"exit");
            var c=Start(Path.Combine(root,"boot failure"),"broken");
            assert(await Until(()=>c.Has("ready.json"),30),"Boot-failure case gets ready");
            c.Signal("commit.json");Stop(c.Parent);
            var rolled=await c.Apply.WaitAsync(TimeSpan.FromSeconds(120));
            Environment.SetEnvironmentVariable(BootVariable,null);
            assert(!rolled.Success&&rolled.Phase=="rolled-back"&&c.Version=="old"&&File.ReadAllText(Path.Combine(c.Workspace,"failed-install","version.txt"))=="broken"&&!Backups(c.Target),
                "A new version that exits before confirming startup is rolled back to the previous installation");
            assert(await Until(()=>Restarts().Contains(c.Target),10),"After a rollback the previous version is reopened");

            // 4. Cancelled before the handoff: nothing moves and nothing is restarted.
            var d=Start(Path.Combine(root,"abort"),"aborted");
            assert(await Until(()=>d.Has("ready.json"),30),"Abort case gets ready");
            int before=Restarts().Length;d.Signal("abort.json");
            var aborted=await d.Apply.WaitAsync(TimeSpan.FromSeconds(30));Stop(d.Parent);
            assert(!aborted.Success&&aborted.Phase=="cancelled"&&d.Version=="old"&&Directory.Exists(d.Stage)&&!Backups(d.Target)&&Restarts().Length==before,
                "Cancelling before the app closes leaves the installation untouched");

            // 5. The app ends without confirming (crash before commit): no replacement.
            var e=Start(Path.Combine(root,"parent gone"),"orphan");
            assert(await Until(()=>e.Has("ready.json"),30),"Parent-exit case gets ready");
            Stop(e.Parent);
            var orphan=await e.Apply.WaitAsync(TimeSpan.FromSeconds(30));
            assert(!orphan.Success&&orphan.Phase=="failed"&&e.Version=="old"&&!Backups(e.Target),"An app that exits without confirming never triggers a replacement");

            // 6-8. Payload and identity checks fail before ready, without touching the installation.
            var f=Start(Path.Combine(root,"tampered"),"tampered",alterStage:stage=>File.AppendAllText(Path.Combine(stage,"engine","rife-poc.exe"),"x"));
            var tampered=await f.Apply.WaitAsync(TimeSpan.FromSeconds(30));Stop(f.Parent);
            assert(!tampered.Success&&tampered.Error.Contains("hash mismatch",StringComparison.OrdinalIgnoreCase)&&!f.Has("ready.json")&&f.Version=="old","A file changed after download is rejected before ready");
            var g=Start(Path.Combine(root,"unlisted"),"unlisted",alterStage:stage=>File.WriteAllText(Path.Combine(stage,"dll","extra.dll"),"x"));
            var unlisted=await g.Apply.WaitAsync(TimeSpan.FromSeconds(30));Stop(g.Parent);
            assert(!unlisted.Success&&unlisted.Error.Contains("Unlisted",StringComparison.Ordinal)&&g.Version=="old","A file not in the manifest is rejected");
            var h=Start(Path.Combine(root,"pid reuse"),"reused",alterRequest:x=>x with{ParentStartedUtcTicks=x.ParentStartedUtcTicks+1});
            var reused=await h.Apply.WaitAsync(TimeSpan.FromSeconds(30));Stop(h.Parent);
            assert(!reused.Success&&reused.Error.Contains("no longer running",StringComparison.Ordinal)&&!h.Has("ready.json")&&h.Version=="old","A reused process ID (different start time) is not treated as the app");

            // 9. Files stay locked longer than the retry limit (the 1.0.0 symptom): keep the old version and reopen it.
            var k=Start(Path.Combine(root,"locked"),"locked");
            assert(await Until(()=>k.Has("ready.json"),30),"Lock case gets ready");
            k.Signal("commit.json");
            int restartsBefore=Restarts().Length;
            InstallResult locked;
            using(new FileStream(Path.Combine(k.Target,"version.txt"),FileMode.Open,FileAccess.Read,FileShare.Read)){
                Stop(k.Parent);locked=await k.Apply.WaitAsync(TimeSpan.FromSeconds(120));
            }
            assert(!locked.Success&&locked.Phase=="failed"&&k.Version=="old"&&Directory.Exists(k.Stage)&&!Backups(k.Target),"Files locked past the retry limit leave the installation untouched");
            assert(await Until(()=>Restarts().Length>restartsBefore&&Restarts().Contains(k.Target),10),"After a failed replacement the current version is reopened instead of leaving nothing running");

            // 10. Concurrent installers for the same folder.
            var m=Start(Path.Combine(root,"concurrent"),"concurrent");
            assert(await Until(()=>m.Has("ready.json"),30),"Concurrent case gets ready");
            var duplicate=Task.Run(()=>UpdateInstaller.Apply(m.Request,Path.Combine(m.Workspace,"runner")));
            var refused=await duplicate.WaitAsync(TimeSpan.FromSeconds(30));
            assert(!refused.Success&&refused.Error.Contains("already installing",StringComparison.Ordinal)&&m.State=="handoff"&&!m.Has("result.json"),
                "A second installer for the same folder is refused without touching the running one");
            m.Signal("abort.json");await m.Apply.WaitAsync(TimeSpan.FromSeconds(30));Stop(m.Parent);

            // 11. Requests that point outside the expected layout are refused before any check runs.
            string layout=Path.Combine(root,"layout");Directory.CreateDirectory(layout);
            var n=Start(layout,"layout");n.Signal("abort.json");await n.Apply.WaitAsync(TimeSpan.FromSeconds(30));Stop(n.Parent);
            string runner=Path.Combine(n.Workspace,"runner");
            bool Refused(InstallRequest request){
                UpdateInstaller.WriteJson(n.Request,request);
                try{UpdateInstaller.ReadRequest(n.Request,runner);return false;}catch(IOException){return true;}
            }
            assert(Refused(n.Value with{Stage=Path.Combine(root,".FrameDoubler-update-elsewhere")}),"Stage outside the installation's parent is refused");
            assert(Refused(n.Value with{Stage=Path.Combine(layout,"FrameDoubler-update-x")}),"Stage without the updater prefix is refused");
            assert(Refused(n.Value with{Token="../not-a-token"}),"Malformed token is refused");
            assert(Refused(n.Value with{Target=n.Value.Stage}),"Target equal to the stage is refused");
            UpdateInstaller.WriteJson(n.Request,n.Value);
            bool wrongRunner=false;try{UpdateInstaller.ReadRequest(n.Request,Path.Combine(layout,"runner"));}catch(IOException){wrongRunner=true;}
            assert(wrongRunner,"A runner outside the request's workspace is refused");
            bool wrongApp=false;try{UpdateInstaller.AcknowledgeStartup(n.Request);}catch(IOException){wrongApp=true;}
            assert(wrongApp&&!n.Has("booted.json"),"Only the newly installed app at the target can confirm startup");
            Console.WriteLine("Installer artifacts: "+root);
        }finally{
            Environment.SetEnvironmentVariable(FakeVariable,previousFake);Environment.SetEnvironmentVariable(BootVariable,previousBoot);
        }
    }
}
