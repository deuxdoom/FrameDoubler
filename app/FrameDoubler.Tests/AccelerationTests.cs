using FrameDoubler.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class AccelerationTests
{
    public static async Task IntegrationAsync(AppPaths paths,DeviceChoice device,Action<bool,string> check){
        if(!Acceleration.Installed(paths)||device.Device=="cpu")return;
        await Acceleration.VerifyInstalledAsync(paths);var devices=await Acceleration.ProbeAsync(paths);
        var accelerator=devices.Single(x=>x.VendorId==device.Gpu!.VendorId&&x.DeviceId==device.Gpu.DeviceId);
        var choice=device with{Backend=Acceleration.Id,AccelerationIndex=accelerator.Index};
        const int w=161,h=91;byte[] a=new byte[w*h*3],b=new byte[a.Length];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)for(int c=0;c<3;c++){int i=(y*w+x)*3+c;a[i]=(byte)(30+x/2+y/3+c*15);b[i]=(byte)(30+Math.Max(0,x-7)/2+y/3+c*15);}
        foreach(string model in new[]{"v4.25","v4.26"})foreach(double scale in new[]{1d,.5d}){
            await using var actual=new NativeEngine(paths,model,w,h,choice,scale);
            await using var reference=new NativeEngine(paths,model,w,h,DevicePolicy.Choose([],"cpu"),scale);
            foreach(float time in new[]{.25f,.5f,.75f}){
                byte[] expected=await reference.InterpolateAsync(a,b,time),output=await actual.InterpolateAsync(a,b,time);
                double absolute=0,square=0;int[] histogram=new int[256];for(int i=0;i<a.Length;i++){int delta=Math.Abs(expected[i]-output[i]);absolute+=delta;square+=delta*delta;histogram[delta]++;}
                int p99=0,total=0;while(p99<255&&(total+=histogram[p99])<a.Length*.99)p99++;
                double psnr=square==0?999:10*Math.Log10(255d*255/(square/a.Length));
                check(psnr>=55&&absolute/a.Length<=.1&&p99<=1,$"Real RTX native pipe/odd crop/timestep/scale versus ncnn CPU: {model}, {scale}, {time}");
            }
        }
        await using(var baseline=new NativeEngine(paths,"v4.25",w,h,device,1)){
            byte[] expected=await baseline.InterpolateAsync(a,b,.5f);
            await using var pool=new EnginePool(1,choice with{RuntimeDirectory=Path.Combine(EncoderDetector.WorkRoot,"absent-runtime")},d=>new NativeEngine(paths,"v4.25",w,h,d,1));
            byte[] output=await pool.InterpolateAsync(a,b,.5f);
            check(pool.Choice.Backend=="vulkan"&&output.SequenceEqual(expected),"Real missing RTX DLL failure retries the request on ncnn Vulkan");
        }
        await using(var engine=new NativeEngine(paths,"v4.26",w,h,choice,1)){
            using var cancelled=new CancellationTokenSource(TimeSpan.FromMilliseconds(5));bool stopped=false;
            try{await engine.InterpolateAsync(a,b,.5f,cancelled.Token);}catch(OperationCanceledException){stopped=true;}
            check(stopped,"RTX native startup/inference observes cancellation");
        }
    }
    public static async Task RunAsync(Action<bool,string> check){
        string root=Path.Combine(EncoderDetector.WorkRoot,"acceleration-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            var preferences=new Preferences{Accelerator="rtx"};preferences.Save(root);
            check(Preferences.Load(root).Accelerator=="rtx","Accelerator preference survives portable INI roundtrip");
            File.WriteAllText(Path.Combine(root,Preferences.FileName),"[FrameDoubler]\nAccelerator=unknown\n");
            check(Preferences.Load(root).Accelerator=="auto","Unknown accelerator preference normalizes to auto");
            string archive=Path.Combine(root,"test.whl");
            void Zip(string name){using var zip=ZipFile.Open(archive,ZipArchiveMode.Create);using var stream=new StreamWriter(zip.CreateEntry(name).Open());stream.Write("fake runtime");}
            AccelerationPackage Package()=>new("test","https://example.invalid/",new FileInfo(archive).Length,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))));
            Zip("package/onnxruntime.dll");var package=Package();string destination=Path.Combine(root,"valid");
            await Acceleration.ExtractVerifiedAsync(package,archive,destination);
            check(File.ReadAllText(Path.Combine(destination,"onnxruntime.dll"))=="fake runtime","Verified wheel extracts the allowed runtime DLL without Python package contents");
            bool denied=false;try{await Acceleration.ExtractVerifiedAsync(package with{Sha256=new string('0',64)},archive,Path.Combine(root,"bad-hash"));}catch(IOException){denied=true;}
            check(denied&&!Directory.Exists(Path.Combine(root,"bad-hash")),"Hash mismatch is refused before extraction");
            File.Delete(archive);Zip("../onnxruntime.dll");denied=false;
            try{await Acceleration.ExtractVerifiedAsync(Package(),archive,Path.Combine(root,"escape"));}catch(IOException){denied=true;}
            check(denied&&!File.Exists(Path.Combine(root,"onnxruntime.dll")),"Verified archive cannot escape its extraction folder");
            var paths=new AppPaths(Path.Combine(root,"engine","rife-poc.exe"),Path.Combine(root,"models"),"","");string runtime=Acceleration.Runtime(paths);Directory.CreateDirectory(runtime);
            var hashes=new Dictionary<string,string>();foreach(string name in new[]{"onnxruntime.dll","onnxruntime_providers_shared.dll","onnxruntime_providers_nv_tensorrt_rtx.dll","cudart64_13.dll","tensorrt_rtx_1_7.dll","tensorrt_onnxparser_rtx_1_7.dll","tensorrt_plugins.dll"}){
                File.WriteAllText(Path.Combine(runtime,name),name);hashes[name]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(runtime,name))));
            }
            File.WriteAllText(Path.Combine(runtime,"installed.json"),JsonSerializer.Serialize(hashes));await Acceleration.VerifyInstalledAsync(paths);
            File.AppendAllText(Path.Combine(runtime,"onnxruntime.dll"),"changed");denied=false;
            try{await Acceleration.VerifyInstalledAsync(paths);}catch(IOException){denied=true;}
            check(denied,"Changed installed DLL is refused before provider loading");
            File.WriteAllText(Path.Combine(root,"keep.txt"),"keep");Acceleration.Remove(paths);
            check(!Acceleration.Installed(paths)&&File.Exists(Path.Combine(root,"keep.txt")),"Add-on removal preserves the rest of the portable installation");
        }finally{Directory.Delete(root,true);}
    }
}
