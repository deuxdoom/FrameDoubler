using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace FrameDoubler.Core;

public sealed record AccelerationPackage(string Name,string Url,long Bytes,string Sha256);
public sealed record AccelerationDevice(int Index,uint VendorId,uint DeviceId);
public static class Acceleration
{
    public const string Id="rtx";
    public const string LicenseTerms="ONNX Runtime: MIT; TensorRT RTX EP: Apache-2.0; included NVIDIA runtime: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html";
    public static AccelerationPackage[] Packages{get;}=[
        new("onnxruntime","https://files.pythonhosted.org/packages/da/50/bcc038601b4c044f07925ac11dc04e8ad4bbea87db9b4b3a2ac0823c6ddb/onnxruntime-1.32.0-cp314-cp314-win_amd64.whl",17123630,"2b08ee990d00ab2ee129c5e039f7339963335a4d76f5bd22bfb4f896ae1cec0a"),
        new("tensorrt-rtx","https://files.pythonhosted.org/packages/ef/64/6a704b972f2c6806b3a13abce3e0d827deb0e4192e7f4240ffdafa8d099c/onnxruntime_ep_nv_tensorrt_rtx_cu13-0.5.0-py3-none-win_amd64.whl",110875664,"609b19f7236c8522ef005a9f763003a78f492e5d871afad39fe1b247760eb3c7")];
    public static long DownloadBytes=>Packages.Sum(x=>x.Bytes);
    public static string Root(AppPaths paths)=>Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(paths.Engine))!,"accel");
    public static string Runtime(AppPaths paths)=>Path.Combine(Root(paths),Id);
    public static string Engine(AppPaths paths)=>Path.Combine(Path.GetDirectoryName(paths.Engine)!,"rife-ort.exe");
    public static string Model(AppPaths paths,string model,double scale)=>Path.Combine(paths.Models,"onnx",model+(scale==.5?"-s05":"-s1")+".onnx");
    public static bool Installed(AppPaths paths)=>File.Exists(Path.Combine(Runtime(paths),"installed.json"));
    private static readonly string[] Required=["onnxruntime.dll","onnxruntime_providers_shared.dll","onnxruntime_providers_nv_tensorrt_rtx.dll","cudart64_13.dll","tensorrt_rtx_1_7.dll","tensorrt_onnxparser_rtx_1_7.dll","tensorrt_plugins.dll"];

    public static async Task<AccelerationDevice[]> ProbeAsync(AppPaths paths,string? runtime=null,CancellationToken cancel=default){
        if(!File.Exists(Engine(paths)))throw new IOException("GPU 가속 엔진이 패키지에 없습니다.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result=await ProcessRunner.RunAsync(Engine(paths),["--probe",runtime??Runtime(paths)],timeout.Token);
        if(result.ExitCode!=0)throw new IOException("GPU 가속 장치를 사용할 수 없습니다: "+result.Error);
        return JsonSerializer.Deserialize<AccelerationDevice[]>(result.Output)??[];
    }
    // Check capabilities without relying on product names. The EP probe is the final authority.
    public static async Task<string> CheckHostAsync(CancellationToken cancel=default){
        string system=Environment.GetFolderPath(Environment.SpecialFolder.System);
        string smi=Path.Combine(system,"nvidia-smi.exe");
        if(!File.Exists(smi))throw new IOException("GPU 드라이버의 기능 조회 도구를 찾을 수 없습니다.");
        var result=await ProcessRunner.RunAsync(smi,["--query-gpu=compute_cap,driver_version","--format=csv,noheader,nounits"],cancel);
        if(result.ExitCode!=0||!result.Output.Split('\n').Any(line=>double.TryParse(line.Split(',')[0].Trim(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double cap)&&cap>=8))
            throw new IOException("추가 기능에 필요한 GPU 연산 기능을 확인하지 못했습니다.");
        return result.Output.Trim();
    }
    public static async Task VerifyInstalledAsync(AppPaths paths,CancellationToken cancel=default){
        string root=Runtime(paths);var manifest=JsonSerializer.Deserialize<Dictionary<string,string>>(await File.ReadAllTextAsync(Path.Combine(root,"installed.json"),cancel))??throw new IOException("잘못된 가속 구성입니다.");
        foreach(string name in Required){
            if(!manifest.TryGetValue(name,out string? expected))throw new IOException("가속 파일 누락: "+name);
            await using var stream=File.OpenRead(Path.Combine(root,name));
            if(!Convert.ToHexString(await SHA256.HashDataAsync(stream,cancel)).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("가속 파일 무결성 검사 실패: "+name);
        }
    }
    public static async Task ExtractVerifiedAsync(AccelerationPackage package,string archive,string destination,CancellationToken cancel=default){
        await using(var stream=File.OpenRead(archive)){
            if(stream.Length!=package.Bytes||!Convert.ToHexString(await SHA256.HashDataAsync(stream,cancel)).Equals(package.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("추가 기능 다운로드 SHA-256 또는 크기 불일치");
        }
        string root=Path.GetFullPath(destination)+Path.DirectorySeparatorChar;Directory.CreateDirectory(root);
        using var zip=ZipFile.OpenRead(archive);long expanded=0;
        foreach(var entry in zip.Entries){
            cancel.ThrowIfCancellationRequested();string relative=entry.FullName.Replace('\\','/');
            if(relative.Split('/').Any(x=>x=="..")||Path.IsPathRooted(relative)||relative.Contains(':'))throw new IOException("추가 기능 압축 경로가 잘못되었습니다.");
            bool dll=Required.Contains(entry.Name,StringComparer.OrdinalIgnoreCase);
            bool license=!relative.EndsWith('/')&&(relative.Contains("LICENSE",StringComparison.OrdinalIgnoreCase)||relative.Contains("NOTICE",StringComparison.OrdinalIgnoreCase)||relative.Contains("/licenses/",StringComparison.OrdinalIgnoreCase)||relative.Contains(".dist-info/",StringComparison.Ordinal)&&entry.Name=="METADATA");
            if(!dll&&!license)continue;
            expanded=checked(expanded+entry.Length);if(expanded>1_000_000_000||entry.Length>500_000_000)throw new IOException("추가 기능 압축 크기 제한 초과");
            string target=Path.GetFullPath(Path.Combine(root,dll?entry.Name:Path.Combine("licenses",package.Name,relative)));
            if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new IOException("추가 기능 압축 경로 이탈");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input=entry.Open();await using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            await input.CopyToAsync(output,cancel);
        }
    }
    public static async Task InstallAsync(AppPaths paths,IProgress<string>? progress=null,CancellationToken cancel=default,Func<AccelerationPackage,string,CancellationToken,Task>? download=null){
        if(!File.Exists(Engine(paths))||new[]{"v4.25","v4.26"}.Any(model=>!File.Exists(Model(paths,model,1))||!File.Exists(Model(paths,model,.5))))throw new IOException("이 빌드에는 GPU 가속 엔진 또는 모델이 없습니다.");
        await CheckHostAsync(cancel);if(Installed(paths))throw new IOException("이미 설치되어 있습니다.");
        string parent=Root(paths);Directory.CreateDirectory(parent);
        string stage=Path.Combine(parent,".rtx-stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try{
            using var client=new HttpClient{Timeout=TimeSpan.FromMinutes(20)};
            foreach(var package in Packages){
                progress?.Report("다운로드: "+package.Name);string archive=Path.Combine(stage,package.Name+".whl");
                if(download is not null)await download(package,archive,cancel);
                else{
                    using var response=await client.GetAsync(package.Url,HttpCompletionOption.ResponseHeadersRead,cancel);response.EnsureSuccessStatusCode();
                    if(response.Content.Headers.ContentLength is {} size&&size!=package.Bytes)throw new IOException("다운로드 크기가 다릅니다.");
                    await using var input=await response.Content.ReadAsStreamAsync(cancel);await using var output=File.Create(archive);
                    byte[] buffer=new byte[81920];long total=0;int count;
                    while((count=await input.ReadAsync(buffer,cancel))>0){total+=count;if(total>package.Bytes)throw new IOException("다운로드 크기 제한 초과");await output.WriteAsync(buffer.AsMemory(0,count),cancel);}
                }
                await ExtractVerifiedAsync(package,archive,stage,cancel);File.Delete(archive);
            }
            var hashes=new Dictionary<string,string>();foreach(string file in Required){await using var stream=File.OpenRead(Path.Combine(stage,file));hashes[file]=Convert.ToHexString(await SHA256.HashDataAsync(stream,cancel));}
            progress?.Report("실제 GPU 추론과 수치 검사 중…");
            var devices=await ProbeAsync(paths,stage,cancel);if(devices.Length==0)throw new IOException("사용 가능한 GPU 가속 장치가 없습니다.");
            await SelfTestAsync(paths,stage,devices[0].Index,cancel);
            await File.WriteAllTextAsync(Path.Combine(stage,"LICENSE-SOURCES.txt"),"ONNX Runtime: MIT; TensorRT RTX EP: Apache-2.0.\nThe included NVIDIA runtime is covered separately by:\nhttps://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html\nDownloads: official PyPI wheels, verified by SHA-256.\n"+string.Join('\n',Packages.Select(x=>x.Url+"\nSHA-256 "+x.Sha256)),cancel);
            await File.WriteAllTextAsync(Path.Combine(stage,"installed.json"),JsonSerializer.Serialize(hashes),cancel);
            cancel.ThrowIfCancellationRequested();Directory.Move(stage,Runtime(paths));progress?.Report("설치 완료");
        }finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }
    private static async Task SelfTestAsync(AppPaths paths,string runtime,int index,CancellationToken cancel){
        const int w=160,h=90;byte[] a=new byte[w*h*3],b=new byte[a.Length];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)for(int c=0;c<3;c++){int p=(y*w+x)*3+c;a[p]=(byte)(x+y+c*11);b[p]=(byte)(Math.Max(0,x-3)+y+c*11);}
        foreach(string model in new[]{"v4.25","v4.26"}){
            var gpu=new DeviceChoice("gpu:0",false,"RTX",null){Backend=Id,AccelerationIndex=index,RuntimeDirectory=runtime};
            await using var accelerated=new NativeEngine(paths,model,w,h,gpu,1);
            await using var baseline=new NativeEngine(paths,model,w,h,DevicePolicy.Choose([],"cpu"),1);
            byte[] actual=await accelerated.InterpolateAsync(a,b,.5f,cancel),expected=await baseline.InterpolateAsync(a,b,.5f,cancel);
            double square=0,absolute=0;int[] histogram=new int[256];for(int i=0;i<a.Length;i++){int d=Math.Abs(actual[i]-expected[i]);histogram[d]++;square+=d*d;absolute+=d;}
            int p99=0,sum=0;while(p99<255&&(sum+=histogram[p99])<a.Length*.99)p99++;
            double psnr=square==0?double.PositiveInfinity:10*Math.Log10(255d*255/(square/a.Length));
            if(psnr<55||absolute/a.Length>.1||p99>1)throw new IOException("추가 기능 자체 수치 검사 실패: "+model);
        }
    }
    public static void Remove(AppPaths paths){string folder=Runtime(paths);if(Directory.Exists(folder))Directory.Delete(folder,true);}
}
