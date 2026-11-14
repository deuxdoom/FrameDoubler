using FrameDoubler.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class UpdateTests
{
    public static void Parse(Action<bool,string> assert){
        static string Release(string tag,string name,string? url=null,string body="")=>JsonSerializer.Serialize(new{tag_name=tag,draft=false,prerelease=false,body,html_url="https://github.com/deuxdoom/FrameDoubler/releases/tag/"+tag,
            assets=new[]{new{name,browser_download_url=url??$"https://github.com/deuxdoom/FrameDoubler/releases/download/{tag}/{name}",size=100,digest="sha256:"+new string('a',64)}}});
        assert(Updates.AssetName(new(1,0,0))=="FrameDoubler.zip"&&Updates.AssetName(new(1,0,1))=="FrameDoubler.zip"&&Updates.AssetName(new(1,0,2))=="FrameDoubler_v102.zip"&&Updates.AssetName(new(2,1,0))=="FrameDoubler_v210.zip",
            "Package names carry the version from 1.0.2; published 1.0.0/1.0.1 keep FrameDoubler.zip");
        string json=Release("v2.1.0","FrameDoubler_v210.zip");
        assert(Updates.Parse(json)?.Installable==true,"Verified versioned release qualifies for update");
        assert(Updates.Parse(json.Replace("sha256:","md5:"))?.Installable==false,"No digest means link-only release");
        assert(Updates.Parse(Release("v2.1.0","FrameDoubler.zip"))?.Installable==false,"Unversioned package name is not installed for a 1.0.2+ release");
        assert(Updates.Parse(Release("v2.1.0","FrameDoubler_v209.zip"))?.Installable==false,"Another version's package is never installed");
        assert(Updates.Parse(Release("v2.1.0","FrameDoubler_v210.zip","https://github.com/deuxdoom/FrameDoubler/releases/download/v2.0.9/FrameDoubler_v210.zip"))?.Installable==false,"Download URL must use this release's tag");
        assert(Updates.Parse(Release("v2.1.0","FrameDoubler_v210.zip",body:"| `FrameDoubler_v210.zip` | `"+new string('b',64)+"` |"))?.Installable==false,"Release-note SHA-256 must agree with the asset digest");
        assert(Updates.Parse(Release("v2.1.0","FrameDoubler_v210.zip",body:"| `FrameDoubler_v210.zip` | `"+new string('a',64)+"` |"))?.Installable==true,"Matching release-note SHA-256 is accepted");
        assert(Updates.Parse(json.Replace("v2.1.0","v1.0.0")) is null,"No update downgrade");
    }
    public static async Task PackageAsync(string archive,Action<bool,string> assert){
        string root=Path.Combine(EncoderDetector.WorkRoot,"update-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var file=File.OpenRead(archive);string hash=Convert.ToHexString(await SHA256.HashDataAsync(file));
        using var package=ZipFile.OpenRead(archive);var manifestEntry=package.Entries.Single(x=>x.FullName.EndsWith("/manifest.json",StringComparison.Ordinal));
        using var manifest=JsonDocument.Parse(manifestEntry.Open());var version=Version.Parse(manifest.RootElement.GetProperty("version").GetString()!);
        bool mismatch=false;try{await Updates.StageAsync(archive,new string('0',64),version,root);}catch(IOException){mismatch=true;}
        assert(mismatch,"Wrong update digest rejected before installation");
        var stage=await Updates.StageAsync(archive,hash,version,root);
        assert(File.Exists(Path.Combine(stage.Directory,"FrameDoubler.exe"))&&File.Exists(Path.Combine(stage.Directory,"engine","rife-poc.exe")),"Actual native ZIP passes all file hashes and staging");
        string unsafeZip=Path.Combine(root,"unsafe.zip");
        using(var zip=ZipFile.Open(unsafeZip,ZipArchiveMode.Create)){
            using(var writer=new StreamWriter(zip.CreateEntry("FrameDoubler/manifest.json").Open()))writer.Write("{}");
            using(var writer=new StreamWriter(zip.CreateEntry("FrameDoubler/../../escaped.txt").Open()))writer.Write("unsafe");
        }
        string badHash;using(var bad=File.OpenRead(unsafeZip))badHash=Convert.ToHexString(await SHA256.HashDataAsync(bad));
        bool escaped=false;try{await Updates.StageAsync(unsafeZip,badHash,new Version(2,0,0),root);}catch(IOException){escaped=true;}
        assert(escaped&&!File.Exists(Path.Combine(Path.GetDirectoryName(root)!,"escaped.txt")),"ZIP traversal rejected");
        // The staged package must also pass the installer's own check (manifest, version, dll layout, no extra files).
        string manifestHash=UpdateInstaller.Hash(Path.Combine(stage.Directory,"manifest.json"));
        string workspace=Path.Combine(root,".FrameDoubler-install-package");
        var request=new InstallRequest(Path.Combine(root,"FrameDoubler"),stage.Directory,workspace,Guid.NewGuid().ToString("N"),Environment.ProcessId,DateTime.UtcNow.Ticks,version.ToString(3),manifestHash,false);
        UpdateInstaller.ValidatePayload(request);
        assert(File.Exists(Path.Combine(stage.Directory,"dll","FrameDoubler.dll"))&&File.Exists(Path.Combine(stage.Directory,"dll","hostfxr.dll"))&&!File.Exists(Path.Combine(stage.Directory,"FrameDoubler.dll")),
            "Actual package keeps the runtime and app DLLs in dll/ and passes the installer payload check");
        Console.WriteLine("Update artifacts: "+root);
    }
}
