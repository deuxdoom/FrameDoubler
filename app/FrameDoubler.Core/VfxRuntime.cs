using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace FrameDoubler.Core;

public sealed record VfxFile(string Name,long Bytes,string Sha256);
// NVIDIA VFX frame generation is not part of the package. The user downloads NVIDIA's own
// wheel after accepting its license; only the five DLLs VideoFrameGeneration imports and
// NVIDIA's license files are kept.
public static class VfxRuntime
{
    public const string Url="https://pypi.nvidia.com/nvidia-vfx/nvidia_vfx-0.2.0.0-cp312-abi3-win_amd64.whl";
    public const long DownloadBytes=435807359;
    public const string DownloadSha256="cebaa12411f7e22b39293205c5c4d07b8073d14ca1e0e7727ee4375207a36445";
    public static VfxFile[] Files{get;}=[
        new("cudart64_12.dll",584304,"663b5fe65aa6c654b8b085a879f49deb19f2cb033b8f2ff8dffd06cdefe0d1db"),
        new("NVCVImage.dll",2694256,"689a64586c804468127647b7ed24acc36a9a9780502ffe95ad11aa4fe3286a39"),
        new("nvngxruntime.dll",83568,"d2dbd1993000a2eed2c7ad8cd417ffc738ea180003daacf190e737121869d637"),
        new("NVVideoEffects.dll",104560,"c6b042fc57bdb6bea88c9c52cb951582377d3f1643acf52a52d197195a1ea8f4"),
        new("nvVFXVideoFrameGeneration.dll",207321712,"270cf4fff9329908f9770d306cb51b910ee04323af5520cd02a98a2e22aba795")];
    public static long InstalledBytes=>Files.Sum(x=>x.Bytes);
    private const string LibraryPrefix="nvvfx/libs/",LicenseMarker=".dist-info/licenses/";

    // Downloaded copy: accel/nvvfx next to engine/, the same place as the other optional add-on.
    public static string DownloadDirectory(string engineDirectory)=>Path.Combine(Path.GetDirectoryName(Path.GetFullPath(engineDirectory).TrimEnd(Path.DirectorySeparatorChar))!,"accel","nvvfx");
    public static bool Complete(string directory)=>Files.All(file=>File.Exists(Path.Combine(directory,file.Name)));
    public static bool Downloaded(string engineDirectory)=>File.Exists(Path.Combine(DownloadDirectory(engineDirectory),"installed.json"))&&Complete(DownloadDirectory(engineDirectory));

    public static async Task InstallAsync(string engineDirectory,IProgress<(double Fraction,string Stage)>? progress=null,CancellationToken cancel=default,
        Func<string,CancellationToken,Task>? download=null){
        string target=DownloadDirectory(engineDirectory),parent=Path.GetDirectoryName(target)!;
        if(Downloaded(engineDirectory))throw new IOException("NVIDIA VFX 런타임이 이미 설치되어 있습니다.");
        Directory.CreateDirectory(parent);
        var drive=new DriveInfo(Path.GetPathRoot(parent)!);
        if(drive.AvailableFreeSpace<DownloadBytes+InstalledBytes+50_000_000)throw new IOException("디스크 공간이 부족합니다. 약 700MB가 필요합니다.");
        string stage=Path.Combine(parent,".nvvfx-stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try{
            string archive=Path.Combine(stage,"nvidia-vfx.whl");
            if(download is not null)await download(archive,cancel);
            else{
                using var client=new HttpClient{Timeout=Timeout.InfiniteTimeSpan};
                using var response=await client.GetAsync(Url,HttpCompletionOption.ResponseHeadersRead,cancel);response.EnsureSuccessStatusCode();
                if(response.Content.Headers.ContentLength is {} size&&size!=DownloadBytes)throw new IOException("다운로드 크기가 다릅니다.");
                await using var input=await response.Content.ReadAsStreamAsync(cancel);await using var output=File.Create(archive);
                byte[] buffer=new byte[1<<20];long total=0;int count;
                while((count=await input.ReadAsync(buffer,cancel))>0){
                    total+=count;if(total>DownloadBytes)throw new IOException("다운로드 크기 제한 초과");
                    await output.WriteAsync(buffer.AsMemory(0,count),cancel);progress?.Report((total/(double)DownloadBytes*.9,"download"));
                }
            }
            progress?.Report((.9,"verify"));
            await ExtractVerifiedAsync(archive,stage,cancel);File.Delete(archive);
            await File.WriteAllTextAsync(Path.Combine(stage,"installed.json"),JsonSerializer.Serialize(Files.ToDictionary(x=>x.Name,x=>x.Sha256)),cancel);
            cancel.ThrowIfCancellationRequested();
            if(Directory.Exists(target))Directory.Delete(target,true);
            Directory.Move(stage,target);progress?.Report((1,"done"));
        }finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }
    public static async Task ExtractVerifiedAsync(string archive,string destination,CancellationToken cancel=default){
        await using(var stream=File.OpenRead(archive)){
            if(stream.Length!=DownloadBytes||!Convert.ToHexString(await SHA256.HashDataAsync(stream,cancel)).Equals(DownloadSha256,StringComparison.OrdinalIgnoreCase))
                throw new IOException("NVIDIA VFX 다운로드의 크기 또는 SHA-256이 다릅니다.");
        }
        await ExtractAsync(archive,destination,Files,cancel);
    }
    // Separate from the wheel check so tests can run it on a small archive with their own file list.
    public static async Task ExtractAsync(string archive,string destination,IReadOnlyList<VfxFile> files,CancellationToken cancel=default){
        string root=Path.GetFullPath(destination)+Path.DirectorySeparatorChar;Directory.CreateDirectory(root);
        using var zip=ZipFile.OpenRead(archive);var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries){
            cancel.ThrowIfCancellationRequested();string relative=entry.FullName.Replace('\\','/');
            if(relative.EndsWith('/'))continue;
            if(relative.Split('/').Any(x=>x=="..")||Path.IsPathRooted(relative)||relative.Contains(':'))throw new IOException("NVIDIA VFX 압축 경로가 잘못되었습니다.");
            var file=relative.StartsWith(LibraryPrefix,StringComparison.OrdinalIgnoreCase)&&relative.IndexOf('/',LibraryPrefix.Length)<0
                ?files.FirstOrDefault(x=>x.Name.Equals(entry.Name,StringComparison.OrdinalIgnoreCase)):null;
            int license=relative.IndexOf(LicenseMarker,StringComparison.OrdinalIgnoreCase);
            if(file is null&&license<0)continue;
            if(file is not null&&entry.Length!=file.Bytes)throw new IOException("NVIDIA VFX 파일 크기가 다릅니다: "+file.Name);
            if(file is null&&entry.Length>8_000_000)continue;
            string target=Path.GetFullPath(Path.Combine(root,file?.Name??Path.Combine("licenses",entry.Name)));
            if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new IOException("NVIDIA VFX 압축 경로 이탈");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using(var input=entry.Open())await using(var output=new FileStream(target,FileMode.Create,FileAccess.Write,FileShare.None))await input.CopyToAsync(output,cancel);
            if(file is null)continue;
            await using var written=File.OpenRead(target);
            if(!Convert.ToHexString(await SHA256.HashDataAsync(written,cancel)).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("NVIDIA VFX 파일 무결성 검사 실패: "+file.Name);
            found.Add(file.Name);
        }
        if(files.FirstOrDefault(x=>!found.Contains(x.Name)) is {} missing)throw new IOException("NVIDIA VFX 파일 누락: "+missing.Name);
    }
    public static void Remove(string engineDirectory){string folder=DownloadDirectory(engineDirectory);if(Directory.Exists(folder))Directory.Delete(folder,true);}
}
