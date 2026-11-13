using FrameDoubler.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class UpdateTests
{
    public static void Parse(Action<bool,string> assert){
        string json=JsonSerializer.Serialize(new{tag_name="v2.1.0",draft=false,prerelease=false,body="",html_url="https://github.com/deuxdoom/FrameDoubler/releases/tag/v2.1.0",
            assets=new[]{new{name="FrameDoubler.Native.zip",browser_download_url="https://github.com/deuxdoom/FrameDoubler/releases/download/v2.1.0/FrameDoubler.Native.zip",size=100,digest="sha256:"+new string('a',64)}}});
        assert(Updates.Parse(json)?.Installable==true,"Verified native release qualifies for update");
        assert(Updates.Parse(json.Replace("sha256:","md5:"))?.Installable==false,"No digest means link-only release");
        assert(Updates.Parse(json.Replace("FrameDoubler.Native.zip","FrameDoubler_v100.zip"))?.Installable==false,"Legacy Torch asset never auto-installs");
        assert(Updates.Parse(json.Replace("v2.1.0","v1.0.0")) is null,"No update downgrade");
    }
    public static async Task PackageAsync(string archive,Action<bool,string> assert){
        string root=Path.Combine(EncoderDetector.WorkRoot,"update-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var file=File.OpenRead(archive);string hash=Convert.ToHexString(await SHA256.HashDataAsync(file));
        bool mismatch=false;try{await Updates.StageAsync(archive,new string('0',64),new Version(2,0,0),root);}catch(IOException){mismatch=true;}
        assert(mismatch,"Wrong update digest rejected before installation");
        var stage=await Updates.StageAsync(archive,hash,new Version(2,0,0),root);
        assert(File.Exists(Path.Combine(stage.Directory,"FrameDoubler.exe"))&&File.Exists(Path.Combine(stage.Directory,"native","rife-poc.exe")),"Actual native ZIP passes all file hashes and staging");
        string unsafeZip=Path.Combine(root,"unsafe.zip");
        using(var zip=ZipFile.Open(unsafeZip,ZipArchiveMode.Create)){
            using(var writer=new StreamWriter(zip.CreateEntry("FrameDoubler.Native/manifest.json").Open()))writer.Write("{}");
            using(var writer=new StreamWriter(zip.CreateEntry("FrameDoubler.Native/../../escaped.txt").Open()))writer.Write("unsafe");
        }
        string badHash;using(var bad=File.OpenRead(unsafeZip))badHash=Convert.ToHexString(await SHA256.HashDataAsync(bad));
        bool escaped=false;try{await Updates.StageAsync(unsafeZip,badHash,new Version(2,0,0),root);}catch(IOException){escaped=true;}
        assert(escaped&&!File.Exists(Path.Combine(Path.GetDirectoryName(root)!,"escaped.txt")),"ZIP traversal rejected");
        string helper=Updates.WriteSwapHelper(Path.Combine(root,"helper"));
        string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        async Task<(ProcessResult Result,string Target,string Backup)> Swap(string suffix,bool missing){
            string target=Path.Combine(root,"fake-install-"+suffix),newDir=Path.Combine(root,".FrameDoubler-update-"+suffix),backup=target+".previous-test";
            Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"FrameDoubler.exe"),"old");
            if(!missing){Directory.CreateDirectory(newDir);File.WriteAllText(Path.Combine(newDir,"FrameDoubler.exe"),"new");}
            var result=await ProcessRunner.RunAsync(shell,["-NoProfile","-ExecutionPolicy","Bypass","-File",helper,"-WaitPid","0","-Target",target,"-Stage",newDir,"-Backup",backup,"-NoRestart"]);
            return(result,target,backup);
        }
        var success=await Swap("success",false);
        assert(success.Result.ExitCode==0&&File.ReadAllText(Path.Combine(success.Target,"FrameDoubler.exe"))=="new"&&File.ReadAllText(Path.Combine(success.Backup,"FrameDoubler.exe"))=="old","Native directory swap preserves backup");
        var failure=await Swap("failure",true);
        assert(failure.Result.ExitCode!=0&&File.ReadAllText(Path.Combine(failure.Target,"FrameDoubler.exe"))=="old","Failed update swap restores old installation");
        Console.WriteLine("Update artifacts: "+root);
    }
}
