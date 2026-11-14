using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace FrameDoubler.Core;

public static class EngineWorkerPolicy
{
    // Conservative committed memory estimates. FP32 activations are used by the
    // product even for RGB8; RGB16 additionally changes the transport, not this policy.
    public static (ulong Gpu,ulong Ram) PerEngine(MediaInfo source,bool halfStorage=false){
        const double gib=1024d*1024*1024;
        double pixels=source.Width*(double)source.Height/1_000_000;
        double storage=source.BitDepth>8||!halfStorage?1.8:1;
        return ((ulong)Math.Ceiling((.7+.2*pixels)*storage*gib),
                (ulong)Math.Ceiling((.9+.25*pixels)*storage*gib));
    }
    public static ulong RequiredGpuBytes(MediaInfo source)=>PerEngine(source).Gpu;
    public static int Count(MediaInfo source,DeviceChoice device,ulong? availableRam,int? requested=null){
        if(device.Backend==Acceleration.Id)return 1; // Separate backend; concurrency is not inferred from Vulkan measurements.
        if(device.Device=="cpu"||device.Gpu?.BudgetBytes is not ulong budget||availableRam is not ulong ram)return 1;
        const double gib=1024d*1024*1024;
        var estimate=PerEngine(source,device.HalfStorage);
        // Large FP32 working sets showed no whole-video throughput gain with a
        // third worker, while retaining another multi-GB GPU/host allocation.
        int maximum=(source.BitDepth>8||!device.HalfStorage)&&estimate.Gpu>=(3UL<<30)?2:3;
        int count=(int)Math.Clamp(Math.Min((budget-gib)/estimate.Gpu,(ram-4*gib)/estimate.Ram),1,maximum);
        return Math.Min(count,Math.Clamp(requested??maximum,1,maximum));
    }
    public static int For(MediaInfo source,DeviceChoice device)=>Count(source,device,AvailableRam(),
        int.TryParse(Environment.GetEnvironmentVariable("FRAMEDOUBLER_ENGINE_WORKERS"),out int n)?n:null);
    public static ulong? AvailableRam(){
        if(!OperatingSystem.IsWindows())return null;
        var status=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};
        return GlobalMemoryStatusEx(ref status)?status.AvailablePhysical:null;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus{
        public uint Length,Load;
        public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

// A lease is held for the entire request. Failed workers are removed independently;
// requests wait for surviving workers before considering CPU fallback.
public sealed class EnginePool:IAsyncDisposable
{
    private sealed class Worker(DeviceChoice device){public DeviceChoice Device=device;public IInterpolationEngine? Engine;}
    private readonly Channel<Worker> idle=Channel.CreateUnbounded<Worker>();
    private readonly List<Worker> workers=[];
    private readonly Func<DeviceChoice,IInterpolationEngine> create;
    private readonly bool allowCpuFallback;
    private readonly Action<DeviceChoice,int>? changed;
    private readonly object sync=new();
    private int count;
    private bool disposed;
    public DeviceChoice Choice{get;private set;}
    public int Count=>Volatile.Read(ref count);
    public EnginePool(int size,DeviceChoice choice,Func<DeviceChoice,IInterpolationEngine> create,bool allowCpuFallback=false,Action<DeviceChoice,int>? changed=null){
        if(size is <1 or >3||choice.Device=="cpu"&&size!=1)throw new ArgumentOutOfRangeException(nameof(size));
        Choice=choice;this.create=create;this.allowCpuFallback=allowCpuFallback;this.changed=changed;count=size;
        for(int i=0;i<size;i++){var worker=new Worker(choice);workers.Add(worker);idle.Writer.TryWrite(worker);}
    }
    public async Task<byte[]> InterpolateAsync(ReadOnlyMemory<byte> first,ReadOnlyMemory<byte> second,float timestep,CancellationToken cancel=default){
        ObjectDisposedException.ThrowIf(disposed,this);
        while(true){
            cancel.ThrowIfCancellationRequested();
            Worker worker;
            try{worker=await idle.Reader.ReadAsync(cancel);}
            catch(ChannelClosedException ex){throw new IOException("All inference workers failed",ex.InnerException??ex);}
            bool healthy=true;
            try{
                worker.Engine??=create(worker.Device);
                return await worker.Engine.InterpolateAsync(first,second,timestep,cancel);
            }catch(Exception ex)when(ex is IOException or TimeoutException or System.ComponentModel.Win32Exception){
                cancel.ThrowIfCancellationRequested();healthy=false;
                if(worker.Engine is not null){await worker.Engine.DisposeAsync();worker.Engine=null;}
                lock(sync){
                    count--;
                    if(count==0){
                        if(Choice.Backend==Acceleration.Id){
                            Choice=Choice with{Backend="vulkan",RuntimeDirectory=null,Explanation="GPU 가속 추가 기능에 실패하여 ncnn Vulkan으로 대체합니다."};
                            var fallback=new Worker(Choice);workers.Add(fallback);count=1;idle.Writer.TryWrite(fallback);
                        }else if(allowCpuFallback&&Choice.Device!="cpu"){
                            Choice=DevicePolicy.Choose([],"cpu");var cpu=new Worker(Choice);workers.Add(cpu);count=1;idle.Writer.TryWrite(cpu);
                        }else idle.Writer.TryComplete(ex);
                    }
                    changed?.Invoke(Choice,count);
                }
            }finally{if(healthy)idle.Writer.TryWrite(worker);}
        }
    }
    // Caller drains/cancels outstanding requests before disposing the pool.
    public async ValueTask DisposeAsync(){
        if(disposed)return;disposed=true;idle.Writer.TryComplete();
        foreach(var worker in workers)if(worker.Engine is not null)await worker.Engine.DisposeAsync();
    }
}
