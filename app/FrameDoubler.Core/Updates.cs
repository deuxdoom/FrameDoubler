using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FrameDoubler.Core;

public sealed record UpdateRelease(Version Version,string Page,string Notes,string? AssetUrl,string? Sha256,long Size)
{public bool Installable=>AssetUrl is not null&&Sha256 is not null;}
public sealed record StagedUpdate(string Directory,Version Version);

public static class Updates
{
    public const string ReleasePage="https://github.com/deuxdoom/FrameDoubler/releases/latest";
    public static Version Current{get;}=new(2,0,0);
    private const long MaximumBytes=640L*1024*1024;
    public static async Task<UpdateRelease?> CheckAsync(CancellationToken cancel=default){
        using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(10)};client.DefaultRequestHeaders.UserAgent.ParseAdd("FrameDoubler/2.0.0");
        using var response=await client.GetAsync("https://api.github.com/repos/deuxdoom/FrameDoubler/releases/latest",cancel);
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();return Parse(await response.Content.ReadAsStringAsync(cancel));
    }
    public static UpdateRelease? Parse(string json){
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        if(root.TryGetProperty("draft",out var draft)&&draft.GetBoolean()||root.TryGetProperty("prerelease",out var preview)&&preview.GetBoolean())return null;
        string tag=root.GetProperty("tag_name").GetString()??"";
        if(!Version.TryParse(tag.TrimStart('v'),out var version)||version<=Current)return null;
        string notes=root.TryGetProperty("body",out var body)?body.GetString()??"":"";
        string page=root.TryGetProperty("html_url",out var p)?p.GetString()??ReleasePage:ReleasePage;
        if(!page.StartsWith("https://github.com/deuxdoom/FrameDoubler/releases/",StringComparison.Ordinal))page=ReleasePage;
        string? url=null,hash=null;long size=0;
        if(root.TryGetProperty("assets",out var assets))foreach(var asset in assets.EnumerateArray()){
            if(asset.GetProperty("name").GetString()!="FrameDoubler.zip")continue;
            string candidate=asset.GetProperty("browser_download_url").GetString()??"";
            string digest=asset.TryGetProperty("digest",out var d)?d.GetString()??"":"";
            size=asset.GetProperty("size").GetInt64();
            if(!candidate.StartsWith("https://github.com/deuxdoom/FrameDoubler/releases/download/",StringComparison.Ordinal)||size<=0||size>MaximumBytes
                ||!Regex.IsMatch(digest,@"^sha256:[0-9a-fA-F]{64}$"))continue;
            hash=digest[7..].ToLowerInvariant();url=candidate;
            // If release notes supply a hash for this asset, it must agree.
            string[] lines=notes.Split('\n');
            for(int i=0;i<lines.Length;i++)if(lines[i].Contains("FrameDoubler.zip",StringComparison.OrdinalIgnoreCase)){
                foreach(var match in Regex.Matches(string.Join('\n',lines.Skip(i).Take(5)),@"\b[0-9a-fA-F]{64}\b").Cast<Match>())
                    if(!string.Equals(hash,match.Value,StringComparison.OrdinalIgnoreCase)){url=null;hash=null;break;}
                break;
            }
            break;
        }
        return new(version,page,notes,url,hash,size);
    }
    public static async Task<StagedUpdate> DownloadAsync(UpdateRelease release,string installationParent,IProgress<double>? progress=null,CancellationToken cancel=default){
        if(!release.Installable)throw new InvalidOperationException("Release has no verified native asset");
        if(!release.AssetUrl!.StartsWith("https://github.com/deuxdoom/FrameDoubler/releases/download/",StringComparison.Ordinal))throw new IOException("Untrusted update URL");
        string cache=Path.Combine(EncoderDetector.WorkRoot,"updates");Directory.CreateDirectory(cache);
        string file=Path.Combine(cache,$"{Guid.NewGuid():N}.zip");
        try{
            using var client=new HttpClient{Timeout=TimeSpan.FromMinutes(10)};client.DefaultRequestHeaders.UserAgent.ParseAdd("FrameDoubler/2.0.0");
            using var response=await client.GetAsync(release.AssetUrl,HttpCompletionOption.ResponseHeadersRead,cancel);response.EnsureSuccessStatusCode();
            using(var output=File.Create(file)){
                using var input=await response.Content.ReadAsStreamAsync(cancel);byte[] buffer=new byte[131072];long count=0;
                while(true){int read=await input.ReadAsync(buffer,cancel);if(read==0)break;count+=read;if(count>MaximumBytes)throw new IOException("Update exceeds the archive limit");
                    await output.WriteAsync(buffer.AsMemory(0,read),cancel);progress?.Report(Math.Min(1,(double)count/Math.Max(1,release.Size)));}
            }
            return await StageAsync(file,release.Sha256!,release.Version,installationParent,cancel);
        }finally{if(File.Exists(file))File.Delete(file);}
    }
    public static async Task<StagedUpdate> StageAsync(string archive,string expectedHash,Version expectedVersion,string parent,CancellationToken cancel=default){
        if(!Regex.IsMatch(expectedHash,@"^[0-9a-fA-F]{64}$"))throw new IOException("Invalid update digest");
        if(new FileInfo(archive).Length>MaximumBytes)throw new IOException("Update archive too large");
        using(var file=File.OpenRead(archive))if(!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(file,cancel)),expectedHash,StringComparison.OrdinalIgnoreCase))
            throw new IOException("Update SHA-256 mismatch");
        parent=Path.GetFullPath(parent);Directory.CreateDirectory(parent);
        string stage=Path.Combine(parent,".FrameDoubler-update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try{
            using var zip=ZipFile.OpenRead(archive);if(zip.Entries.Count>4000)throw new IOException("Too many update files");
            var manifestEntries=zip.Entries.Where(x=>x.FullName.Replace('\\','/').TrimEnd('/').Split('/').Last()=="manifest.json").ToArray();
            if(manifestEntries.Length!=1)throw new IOException("Update needs one package manifest");
            string manifestName=manifestEntries[0].FullName.Replace('\\','/');string prefix=manifestName[..^"manifest.json".Length];
            long total=0;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in zip.Entries){
                cancel.ThrowIfCancellationRequested();string name=entry.FullName.Replace('\\','/');
                if(name.TrimEnd('/')==prefix.TrimEnd('/'))continue;
                if(!name.StartsWith(prefix,StringComparison.Ordinal)||name.Contains(':')||((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new IOException("Unsafe update entry");
                string relative=name[prefix.Length..];if(string.IsNullOrEmpty(relative))continue;
                string target=Path.GetFullPath(Path.Combine(stage,relative));
                if(!target.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!names.Add(relative))throw new IOException("Unsafe or duplicate update path");
                total=checked(total+entry.Length);if(total>MaximumBytes)throw new IOException("Update expanded size exceeds limit");
                if(name.EndsWith('/')){Directory.CreateDirectory(target);continue;}
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source=entry.Open();using var dest=File.Create(target);await source.CopyToAsync(dest,cancel);
            }
            using var document=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(stage,"manifest.json"),cancel));var manifest=document.RootElement;
            if(manifest.GetProperty("format").GetString()!="framedoubler-native-v1"||manifest.GetProperty("version").GetString()!=expectedVersion.ToString(3)
                ||!manifest.GetProperty("runtime").GetString()!.Contains("win-x64",StringComparison.Ordinal))throw new IOException("Wrong native package/version");
            var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"manifest.json"};
            foreach(var item in manifest.GetProperty("files").EnumerateArray()){
                string name=item.GetProperty("path").GetString()??"";string target=Path.GetFullPath(Path.Combine(stage,name));
                if(!target.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!expected.Add(name))throw new IOException("Invalid package manifest path");
                using var input=File.OpenRead(target);
                if(input.Length!=item.GetProperty("bytes").GetInt64()||!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input,cancel)),item.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Update file hash mismatch: "+name);
            }
            var actual=Directory.GetFiles(stage,"*",SearchOption.AllDirectories).Select(x=>Path.GetRelativePath(stage,x).Replace('\\','/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(!expected.SetEquals(actual)||!actual.Contains("FrameDoubler.exe")||!actual.Contains("engine/rife-poc.exe"))throw new IOException("Incomplete/unlisted update files");
            return new(stage,expectedVersion);
        }catch{
            // Only the unique directory created above may be removed.
            string resolved=Path.GetFullPath(stage),basePath=parent+Path.DirectorySeparatorChar;
            if(resolved.StartsWith(basePath,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(resolved).StartsWith(".FrameDoubler-update-",StringComparison.Ordinal))Directory.Delete(resolved,true);
            throw;
        }
    }
    public static string WriteSwapHelper(string directory){
        Directory.CreateDirectory(directory);string helper=Path.Combine(directory,"apply-native-update.ps1");File.WriteAllText(helper,SwapScript);return helper;
    }
    public static void LaunchInstaller(StagedUpdate update,string installation){
        installation=Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation));
        string parent=Path.GetDirectoryName(installation)??throw new IOException("Invalid installation root");
        if(Path.GetDirectoryName(Path.GetFullPath(update.Directory))!=parent)throw new IOException("Update stage must be beside installation");
        string backup=installation+".previous-"+Guid.NewGuid().ToString("N"),helper=WriteSwapHelper(Path.Combine(EncoderDetector.WorkRoot,"updates"));
        string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        var info=new ProcessStartInfo(shell){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        foreach(string argument in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",helper,"-WaitPid",Environment.ProcessId.ToString(),"-Target",installation,"-Stage",update.Directory,"-Backup",backup})info.ArgumentList.Add(argument);
        _=Process.Start(info)??throw new IOException("Cannot start update helper");
    }
    public const string SwapScript="""
param([int]$WaitPid,[string]$Target,[string]$Stage,[string]$Backup,[switch]$NoRestart)
$ErrorActionPreference='Stop'
$Target=[IO.Path]::GetFullPath($Target).TrimEnd('\')
$Stage=[IO.Path]::GetFullPath($Stage).TrimEnd('\')
$Backup=[IO.Path]::GetFullPath($Backup).TrimEnd('\')
$Parent=[IO.Path]::GetDirectoryName($Target)
if($Target.Length -le [IO.Path]::GetPathRoot($Target).Length+3 -or [IO.Path]::GetDirectoryName($Stage) -ne $Parent -or [IO.Path]::GetDirectoryName($Backup) -ne $Parent){throw 'Unsafe update paths'}
if(-not [IO.Path]::GetFileName($Stage).StartsWith('.FrameDoubler-update-') -or -not $Backup.StartsWith($Target+'.previous-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe stage/backup names'}
if(Test-Path -LiteralPath $Backup){throw 'Backup already exists'}
if($WaitPid -gt 0){Wait-Process -Id $WaitPid -ErrorAction SilentlyContinue}
$Moved=$false
try{
    Move-Item -LiteralPath $Target -Destination $Backup
    $Moved=$true
    Move-Item -LiteralPath $Stage -Destination $Target
    if(-not $NoRestart){Start-Process -FilePath (Join-Path $Target 'FrameDoubler.exe')}
}catch{
    if($Moved){
        if((Test-Path -LiteralPath $Target) -and -not (Test-Path -LiteralPath $Stage)){Move-Item -LiteralPath $Target -Destination $Stage}
        if(-not (Test-Path -LiteralPath $Target)){Move-Item -LiteralPath $Backup -Destination $Target}
    }
    $_ | Out-String | Set-Content -LiteralPath ($Target+'.update-error.txt')
    throw
}
""";
}
