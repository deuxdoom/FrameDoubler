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
    // The assembly version comes from <Version> in app/Directory.Build.props (single source of truth).
    public static Version Current{get;}=FromAssembly();
    private static Version FromAssembly(){var v=typeof(Updates).Assembly.GetName().Version??new Version(0,0,0);return new(v.Major,v.Minor,Math.Max(0,v.Build));}
    private static string UserAgent=>$"FrameDoubler/{Current}";
    private const long MaximumBytes=640L*1024*1024;
    // 1.0.0 and 1.0.1 were published as FrameDoubler.zip (their updaters look only for that name).
    // From 1.0.2 the package name carries the version digits, e.g. FrameDoubler_v102.zip.
    public static string AssetName(Version version)=>version<new Version(1,0,2)?"FrameDoubler.zip":$"FrameDoubler_v{version.Major}{version.Minor}{Math.Max(0,version.Build)}.zip";
    public static async Task<UpdateRelease?> CheckAsync(CancellationToken cancel=default){
        using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(10)};client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
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
        string? url=null,hash=null;long size=0;string name=AssetName(version);
        if(root.TryGetProperty("assets",out var assets))foreach(var asset in assets.EnumerateArray()){
            // Only the package named for this exact release, downloaded from this exact tag, is installable.
            if(asset.GetProperty("name").GetString()!=name)continue;
            string candidate=asset.GetProperty("browser_download_url").GetString()??"";
            string digest=asset.TryGetProperty("digest",out var d)?d.GetString()??"":"";
            size=asset.GetProperty("size").GetInt64();
            if(candidate!=$"https://github.com/deuxdoom/FrameDoubler/releases/download/{Uri.EscapeDataString(tag)}/{name}"||size<=0||size>MaximumBytes
                ||!Regex.IsMatch(digest,@"^sha256:[0-9a-fA-F]{64}$"))continue;
            hash=digest[7..].ToLowerInvariant();url=candidate;
            // If release notes supply a hash for this asset, it must agree.
            string[] lines=notes.Split('\n');
            for(int i=0;i<lines.Length;i++)if(lines[i].Contains(name,StringComparison.OrdinalIgnoreCase)){
                foreach(var match in Regex.Matches(string.Join('\n',lines.Skip(i).Take(5)),@"\b[0-9a-fA-F]{64}\b").Cast<Match>())
                    if(!string.Equals(hash,match.Value,StringComparison.OrdinalIgnoreCase)){url=null;hash=null;break;}
                break;
            }
            break;
        }
        return new(version,page,notes,url,hash,size);
    }
    // Release bodies are Markdown (RELEASE.md). The update window shows only what changed: headings become
    // plain lines, bullets "•", and install/verification sections, tables, code blocks and rules are dropped.
    public static string ReadableNotes(string markdown){
        var output=new List<string>();bool code=false,skip=false,heading=false;
        static string Inline(string text)=>Regex.Replace(Regex.Replace(Regex.Replace(text,@"\[([^\]]+)\]\([^)]+\)","$1"),@"(\*\*|__)(.+?)\1","$2"),@"`([^`]+)`","$1");
        // No blank line between a heading and its list.
        void Gap(){if(output.Count>0&&output[^1].Length>0&&!heading)output.Add("");}
        foreach(string raw in (markdown??"").Replace("\r\n","\n").Split('\n')){
            string line=raw.TrimEnd(),trimmed=line.Trim();
            if(trimmed.StartsWith("```",StringComparison.Ordinal)){code=!code;continue;}
            if(code)continue;
            if(trimmed.StartsWith('#')){
                string title=Inline(trimmed.TrimStart('#').Trim());
                skip=Regex.IsMatch(title,"설치|검증|Install|Verif|SHA",RegexOptions.IgnoreCase);
                if(!skip&&!title.Contains("What's New",StringComparison.OrdinalIgnoreCase)){heading=false;Gap();output.Add(title);heading=true;}
                continue;
            }
            if(skip||trimmed.StartsWith('|')||Regex.IsMatch(trimmed,@"^(-{3,}|\*{3,}|_{3,})$"))continue;
            if(trimmed.Length==0){Gap();continue;}
            var bullet=Regex.Match(line,@"^(\s*)[-*+]\s+(.*)$");
            output.Add(bullet.Success?new string(' ',bullet.Groups[1].Length)+"• "+Inline(bullet.Groups[2].Value):Inline(trimmed));heading=false;
        }
        while(output.Count>0&&output[^1].Length==0)output.RemoveAt(output.Count-1);
        while(output.Count>0&&output[0].Length==0)output.RemoveAt(0);
        return string.Join(Environment.NewLine,output);
    }
    public static async Task<StagedUpdate> DownloadAsync(UpdateRelease release,string installationParent,IProgress<double>? progress=null,CancellationToken cancel=default){
        if(!release.Installable)throw new InvalidOperationException("Release has no verified native asset");
        if(!release.AssetUrl!.StartsWith("https://github.com/deuxdoom/FrameDoubler/releases/download/",StringComparison.Ordinal))throw new IOException("Untrusted update URL");
        string cache=Path.Combine(EncoderDetector.WorkRoot,"updates");Directory.CreateDirectory(cache);
        string file=Path.Combine(cache,$"{Guid.NewGuid():N}.zip");
        try{
            using var client=new HttpClient{Timeout=TimeSpan.FromMinutes(10)};client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
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
            if(!expected.SetEquals(actual)||!actual.Contains("FrameDoubler.exe")||!actual.Contains("engine/rife-poc.exe")
                ||expectedVersion>=new Version(1,0,2)&&(!actual.Contains("dll/FrameDoubler.dll")||!actual.Contains("dll/hostfxr.dll")))throw new IOException("Incomplete/unlisted update files");
            return new(stage,expectedVersion);
        }catch{
            // Only the unique directory created above may be removed.
            string resolved=Path.GetFullPath(stage),basePath=parent+Path.DirectorySeparatorChar;
            if(resolved.StartsWith(basePath,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(resolved).StartsWith(".FrameDoubler-update-",StringComparison.Ordinal))Directory.Delete(resolved,true);
            throw;
        }
    }
}
