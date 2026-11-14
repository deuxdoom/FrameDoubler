using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FrameDoubler.Core;

public sealed record InstallRequest(string Target,string Stage,string Workspace,string Token,int ParentPid,long ParentStartedUtcTicks,string Version,string ManifestSha256,bool English);
public sealed record InstallProgress(string Phase,string Detail);
public sealed record InstallResult(bool Success,string Phase,string Error,string Backup,string Version);

// The runner and its runtime live outside both directories being replaced. The main app exits only
// after this process has validated the payload and opened the exact parent process handle.
public static class UpdateInstaller
{
    public const string RequestName="request.json";
    // Logs stay readable with Korean folder names (these files are never embedded in HTML).
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public static void WriteJson<T>(string path,T value){
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temporary,JsonSerializer.Serialize(value,Json),new UTF8Encoding(false));File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    private static bool Same(string a,string b)=>string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),StringComparison.OrdinalIgnoreCase);
    private static bool Within(string path,string root)=>Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    public static InstallRequest ReadRequest(string file,string runnerRoot){
        if(new FileInfo(file).Length>65536)throw new IOException("Invalid update request size");
        var request=JsonSerializer.Deserialize<InstallRequest>(File.ReadAllText(file))??throw new IOException("Invalid update request");
        string parent=Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Target)))??throw new IOException("Invalid target");
        if(!Same(file,Path.Combine(request.Workspace,RequestName))||!Same(runnerRoot,Path.Combine(request.Workspace,"runner"))
            ||!Same(Path.GetDirectoryName(request.Stage)!,parent)||!Same(Path.GetDirectoryName(request.Workspace)!,parent)
            ||!Path.GetFileName(request.Stage).StartsWith(".FrameDoubler-update-",StringComparison.Ordinal)
            ||!Path.GetFileName(request.Workspace).StartsWith(".FrameDoubler-install-",StringComparison.Ordinal)
            ||Same(request.Target,request.Stage)||Same(request.Target,request.Workspace)||Same(request.Stage,request.Workspace)
            ||!Regex.IsMatch(request.Token??"","^[a-f0-9]{32}$")||!Regex.IsMatch(request.ManifestSha256??"","^[A-Fa-f0-9]{64}$")
            ||request.ParentPid<=0||request.ParentStartedUtcTicks<=0||!System.Version.TryParse(request.Version,out _))throw new IOException("Invalid update paths or identity");
        foreach(string path in new[]{request.Target,request.Stage,request.Workspace})
            if(Directory.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked update directories are not supported");
        return request;
    }
    public static void ValidatePayload(InstallRequest request){
        string manifestPath=Path.Combine(request.Stage,"manifest.json");
        if(!string.Equals(Hash(manifestPath),request.ManifestSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Update manifest changed");
        using var document=JsonDocument.Parse(File.ReadAllText(manifestPath));var manifest=document.RootElement;
        if(manifest.GetProperty("format").GetString()!="framedoubler-native-v1"||manifest.GetProperty("version").GetString()!=request.Version
            ||!manifest.GetProperty("runtime").GetString()!.Contains("win-x64",StringComparison.Ordinal))throw new IOException("Wrong native package/version");
        var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"manifest.json"};
        foreach(var item in manifest.GetProperty("files").EnumerateArray()){
            string name=item.GetProperty("path").GetString()??"",file=Path.GetFullPath(Path.Combine(request.Stage,name));
            if(!Within(file,request.Stage)||name.Contains(':')||!expected.Add(name.Replace('\\','/'))||(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Invalid package manifest path");
            if(new FileInfo(file).Length!=item.GetProperty("bytes").GetInt64()||!string.Equals(Hash(file),item.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))
                throw new IOException("Update file hash mismatch: "+name);
        }
        foreach(string name in new[]{"FrameDoubler.exe","engine/rife-poc.exe","dll/FrameDoubler.dll","dll/hostfxr.dll"})
            if(!expected.Contains(name))throw new IOException("Incomplete update: "+name);
        foreach(string name in new[]{Preferences.FileName,"settings.json","performance.json"})expected.Add(name);
        foreach(string file in Directory.EnumerateFiles(request.Stage,"*",SearchOption.AllDirectories))
            if(!expected.Contains(Path.GetRelativePath(request.Stage,file).Replace('\\','/')))throw new IOException("Unlisted update file");
    }
    public static async Task<InstallerSession> PrepareAsync(StagedUpdate staged,string target,bool english,CancellationToken cancel=default){
        target=Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        string parent=Path.GetDirectoryName(target)??throw new IOException("Invalid installation root");
        if(!Same(Path.GetDirectoryName(Path.GetFullPath(staged.Directory))!,parent))throw new IOException("Stage must be beside installation");
        string workspace=Path.Combine(parent,".FrameDoubler-install-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        using var self=Process.GetCurrentProcess();
        var request=new InstallRequest(target,Path.GetFullPath(staged.Directory),workspace,Guid.NewGuid().ToString("N"),Environment.ProcessId,self.StartTime.ToUniversalTime().Ticks,
            staged.Version.ToString(3),Hash(Path.Combine(staged.Directory,"manifest.json")),english);
        string file=Path.Combine(workspace,RequestName),runner=Path.Combine(workspace,"runner");
        await Task.Run(()=>{
            ValidatePayload(request);cancel.ThrowIfCancellationRequested();Directory.CreateDirectory(runner);
            string source=Directory.Exists(Path.Combine(AppPaths.InstallationDirectory,"dll"))?AppPaths.InstallationDirectory:staged.Directory;
            File.Copy(Path.Combine(source,"FrameDoubler.exe"),Path.Combine(runner,"FrameDoubler.exe"));
            foreach(string original in Directory.EnumerateFiles(Path.Combine(source,"dll"),"*",SearchOption.AllDirectories)){
                cancel.ThrowIfCancellationRequested();string destination=Path.Combine(runner,Path.GetRelativePath(source,original));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(original,destination);
            }
            WriteJson(file,request);
        },cancel);
        var start=new ProcessStartInfo(Path.Combine(runner,"FrameDoubler.exe")){UseShellExecute=false,WorkingDirectory=runner};
        start.ArgumentList.Add("--apply-update");start.ArgumentList.Add(file);
        // No ChildProcesses.Track: the installer must survive the main app and its kill-on-close job.
        var process=Process.Start(start)??throw new IOException("Cannot start the update installer");
        var session=new InstallerSession(request,process);
        try{
            var watch=Stopwatch.StartNew();
            while(!Marker(request,"ready.json")){
                cancel.ThrowIfCancellationRequested();
                if(process.HasExited){var result=ReadResult(request.Workspace);throw new IOException(result?.Error??"Installer exited before it was ready");}
                if(watch.Elapsed>TimeSpan.FromSeconds(45))throw new IOException("The update installer did not become ready. The current app remains open.");
                await Task.Delay(100,cancel);
            }
            return session;
        }catch{session.Dispose();throw;}
    }
    public sealed class InstallerSession:IDisposable{
        public InstallRequest Request{get;}
        private readonly Process process;private bool committed;
        internal InstallerSession(InstallRequest request,Process process){Request=request;this.process=process;}
        public void Commit(){WriteJson(Path.Combine(Request.Workspace,"commit.json"),new{Request.Token});committed=true;}
        // If abort.json cannot be written the installer still gives up on its own after the confirmation timeout.
        public void Dispose(){
            if(!committed)try{WriteJson(Path.Combine(Request.Workspace,"abort.json"),new{Request.Token});}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
            process.Dispose();
        }
    }
    private static bool Marker(InstallRequest request,string name){
        try{using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(request.Workspace,name)));return document.RootElement.GetProperty("Token").GetString()==request.Token;}
        catch(Exception ex)when(ex is IOException or JsonException or KeyNotFoundException){return false;}
    }
    public static InstallResult? ReadResult(string workspace){
        try{return JsonSerializer.Deserialize<InstallResult>(File.ReadAllText(Path.Combine(workspace,"result.json")));}catch(Exception ex)when(ex is IOException or JsonException){return null;}
    }
    public static void Retry(Action operation,TimeSpan limit,IProgress<InstallProgress>? progress=null){
        var watch=Stopwatch.StartNew();
        while(true){try{operation();return;}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){
            if(watch.Elapsed>=limit)throw;progress?.Report(new("retry",ex.Message));Thread.Sleep(250);
        }}
    }
    private static void ProbeWritable(string directory){
        string file=Path.Combine(directory,".FrameDoubler-write-"+Guid.NewGuid().ToString("N"));
        using var probe=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete,1,FileOptions.DeleteOnClose);
    }
    public static InstallResult Apply(string file,string runnerRoot,IProgress<InstallProgress>? progress=null,CancellationToken cancel=default){
        InstallRequest request=ReadRequest(file,runnerRoot);
        string backup=request.Target+".previous-"+Path.GetFileName(request.Workspace)[".FrameDoubler-install-".Length..];
        string failed=Path.Combine(request.Workspace,"failed-install");
        bool moved=false,installed=false,parentExited=false,accelerationMoved=false;Process? launched=null;
        string phase="prepare";
        // Logs are best effort: a full disk must never skip the rollback that follows.
        void Log(string name,object value){try{WriteJson(Path.Combine(request.Workspace,name),value);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}}
        void State(string next,string detail=""){phase=next;Log("state.json",new{Phase=next,Detail=detail,request.Version,request.Target,Backup=backup});progress?.Report(new(next,detail));}
        InstallResult Finish(bool success,string error){
            // The backup path is reported only while that folder exists (kept after success, or left by a failed recovery).
            var result=new InstallResult(success,phase,error,Directory.Exists(backup)?backup:"",request.Version);Log("result.json",result);
            if(Directory.Exists(request.Target))try{WriteJson(Path.Combine(request.Target,"work","updates","last-install.json"),result);}catch(IOException){}catch(UnauthorizedAccessException){}
            return result;
        }
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Target)).ToUpperInvariant())));
        using var mutex=new Mutex(false,"Local\\FrameDoubler.Install."+key);
        bool owned;
        try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
        // A second installer for the same folder leaves the running one (and its workspace files) alone.
        if(!owned)return new InstallResult(false,"busy","Another update is already installing here","",request.Version);
        try{
            using var parent=Process.GetProcessById(request.ParentPid);
            if(parent.StartTime.ToUniversalTime().Ticks!=request.ParentStartedUtcTicks||parent.HasExited)throw new IOException("The original app is no longer running");
            if(Directory.Exists(backup)||Directory.Exists(failed))throw new IOException("An update backup already exists");
            State("verify");ValidatePayload(request);ProbeWritable(Path.GetDirectoryName(request.Target)!);ProbeWritable(request.Target);
            WriteJson(Path.Combine(request.Workspace,"ready.json"),new{request.Token,Pid=Environment.ProcessId});
            State("handoff");var watch=Stopwatch.StartNew();
            while(!Marker(request,"commit.json")){
                if(Marker(request,"abort.json"))throw new OperationCanceledException();cancel.ThrowIfCancellationRequested();
                if(parent.HasExited)throw new IOException("The app exited before confirming the update");
                if(watch.Elapsed>TimeSpan.FromSeconds(90))throw new IOException("Update confirmation timed out");Thread.Sleep(100);
            }
            State("waiting");watch.Restart();
            while(!parent.WaitForExit(100)){
                if(watch.Elapsed>TimeSpan.FromSeconds(60))throw new IOException("The app did not finish closing. No files were replaced.");
            }
            parentExited=true;cancel.ThrowIfCancellationRequested();Thread.Sleep(350);
            State("backup");Retry(()=>Directory.Move(request.Target,backup),TimeSpan.FromSeconds(30),progress);moved=true;
            State("install");Retry(()=>Directory.Move(request.Stage,request.Target),TimeSpan.FromSeconds(30),progress);installed=true;
            string savedAcceleration=Path.Combine(backup,"accel");
            if(Directory.Exists(savedAcceleration)){
                if((File.GetAttributes(savedAcceleration)&FileAttributes.ReparsePoint)!=0)throw new IOException("Additional runtime directory must not be a link");
                Retry(()=>Directory.Move(savedAcceleration,Path.Combine(request.Target,"accel")),TimeSpan.FromSeconds(30),progress);accelerationMoved=true;
            }
            State("starting");
            var start=new ProcessStartInfo(Path.Combine(request.Target,"FrameDoubler.exe")){UseShellExecute=false,WorkingDirectory=request.Target};
            start.ArgumentList.Add("--updated");start.ArgumentList.Add(file);
            launched=Process.Start(start)??throw new IOException("Cannot restart the new version");
            watch.Restart();
            while(!Booted(request,launched.Id)){
                if(launched.HasExited&&!Booted(request,launched.Id))throw new IOException("The new version exited before confirming startup");
                if(watch.Elapsed>TimeSpan.FromSeconds(60))throw new IOException("The new version did not confirm startup");Thread.Sleep(100);
            }
            State("done");return Finish(true,"");
        }catch(Exception ex){
            string cause=ex.Message;
            if(moved){
                try{
                    State("rollback",cause);
                    if(launched is not null&&!launched.HasExited){launched.Kill(entireProcessTree:true);launched.WaitForExit(10000);}
                    if(accelerationMoved)Retry(()=>Directory.Move(Path.Combine(request.Target,"accel"),Path.Combine(backup,"accel")),TimeSpan.FromSeconds(30),progress);
                    if(installed)Retry(()=>Directory.Move(request.Target,failed),TimeSpan.FromSeconds(30),progress);
                    Retry(()=>Directory.Move(backup,request.Target),TimeSpan.FromSeconds(30),progress);
                    State("rolled-back",cause);
                }catch(Exception recovery){State("recovery-failed",cause+"; "+recovery.Message);return Finish(false,cause+"; "+recovery.Message);}
            }else State(ex is OperationCanceledException?"cancelled":"failed",cause);
            if(parentExited&&File.Exists(Path.Combine(request.Target,"FrameDoubler.exe")))try{
                Process.Start(new ProcessStartInfo(Path.Combine(request.Target,"FrameDoubler.exe")){UseShellExecute=false,WorkingDirectory=request.Target});
            }catch(Exception restart){cause+="; restart: "+restart.Message;}
            return Finish(false,cause);
        }finally{launched?.Dispose();if(owned)mutex.ReleaseMutex();}
    }
    private static bool Booted(InstallRequest request,int pid){
        try{using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(request.Workspace,"booted.json")));var value=document.RootElement;
            return value.GetProperty("Token").GetString()==request.Token&&value.GetProperty("Version").GetString()==request.Version&&value.GetProperty("Pid").GetInt32()==pid;
        }catch(Exception ex)when(ex is IOException or JsonException or KeyNotFoundException){return false;}
    }
    public static void AcknowledgeStartup(string file){
        var request=ReadRequest(file,Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!,"runner"));
        if(!Same(request.Target,AppPaths.InstallationDirectory)||request.Version!=Updates.Current.ToString(3))throw new IOException("Update startup identity does not match");
        WriteJson(Path.Combine(request.Workspace,"booted.json"),new{request.Token,Version=Updates.Current.ToString(3),Pid=Environment.ProcessId});
    }
    // Housekeeping at startup, only for folders this updater created beside the installation:
    // finished installer workspaces are removed (failed ones keep their small JSON logs only), and of the
    // "<install>.previous-*" backups just the newest one is kept so repeated updates do not pile up copies.
    public static void CleanupFinished(string target){
        target=Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        string? parent=Path.GetDirectoryName(target);if(parent is null||!Directory.Exists(parent))return;
        static void TryDelete(string path){try{if(Directory.Exists(path))Directory.Delete(path,true);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}}
        foreach(string workspace in Directory.EnumerateDirectories(parent,".FrameDoubler-install-*")){
            if((File.GetAttributes(workspace)&FileAttributes.ReparsePoint)!=0)continue;
            var result=ReadResult(workspace);if(result is null)continue;   // still running or never started
            // The runner copy goes first: while it is still exiting it stays locked and the logs are kept for a later start.
            TryDelete(Path.Combine(workspace,"runner"));
            if(result.Success){if(!Directory.Exists(Path.Combine(workspace,"runner")))TryDelete(workspace);continue;}
            TryDelete(Path.Combine(workspace,"failed-install"));
            // A download that was verified but never moved into place (e.g. the app did not exit in time).
            try{var request=ReadRequest(Path.Combine(workspace,RequestName),Path.Combine(workspace,"runner"));if(Same(request.Target,target))TryDelete(request.Stage);}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){}
        }
        var backups=Directory.EnumerateDirectories(parent,Path.GetFileName(target)+".previous-*")
            .Where(x=>(File.GetAttributes(x)&FileAttributes.ReparsePoint)==0).OrderByDescending(Directory.GetCreationTimeUtc).Skip(1);
        foreach(string backup in backups)TryDelete(backup);
    }
    // A stage that was downloaded but never installed (preparation failed or was cancelled).
    public static void DiscardStage(StagedUpdate staged){
        string path=Path.GetFullPath(staged.Directory);
        if(Path.GetFileName(path).StartsWith(".FrameDoubler-update-",StringComparison.Ordinal))try{Directory.Delete(path,true);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
    }
}
