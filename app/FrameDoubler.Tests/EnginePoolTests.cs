using FrameDoubler.Core;

internal static class EnginePoolTests
{
    private sealed class Fake(Func<CancellationToken,Task<byte[]>> run,Action? dispose=null):IInterpolationEngine{
        public Task<byte[]> InterpolateAsync(ReadOnlyMemory<byte> a,ReadOnlyMemory<byte> b,float t,CancellationToken cancel=default)=>run(cancel);
        public ValueTask DisposeAsync(){dispose?.Invoke();return ValueTask.CompletedTask;}
    }
    public static async Task RunAsync(Action<bool,string> check){
        var source=new MediaInfo("",3840,2160,"h264","yuv420p",8,1,30,30,false,1,[],null,null,null,null,[]);
        // Explicit legacy half-storage choice for the memory-estimate comparison.
        var gpu=new DeviceChoice("gpu:0",true,"test",new GpuDevice{Index=0,BudgetBytes=16UL<<30,MaxWorkgroupInvocations=256});
        check(EngineWorkerPolicy.Count(source,gpu,32UL<<30)==3,"Large measured budgets allow three workers");
        check(EngineWorkerPolicy.Count(source,gpu with{Gpu=gpu.Gpu! with{BudgetBytes=4UL<<30}},32UL<<30)==1,"4K workers respect GPU reserve");
        check(EngineWorkerPolicy.Count(source,gpu,6UL<<30)==1&&EngineWorkerPolicy.Count(source,gpu,32UL<<30,2)==2,"RAM reserve and explicit worker ceiling");
        check(EngineWorkerPolicy.Count(source,gpu with{Gpu=gpu.Gpu! with{BudgetBytes=null}},32UL<<30)==1&&EngineWorkerPolicy.Count(source,gpu,null)==1,"Unknown memory stays single worker");
        check(EngineWorkerPolicy.Count(source with{BitDepth=10},gpu,32UL<<30)==2&&EngineWorkerPolicy.Count(source,DevicePolicy.Choose([],"cpu"),32UL<<30,3)==1,"Large deep frames are bounded to two workers and CPU to one");
        var smaller=gpu with{Gpu=gpu.Gpu! with{BudgetBytes=8UL<<30}};
        check(EngineWorkerPolicy.Count(source,smaller,32UL<<30)==2&&EngineWorkerPolicy.Count(source with{BitDepth=10},smaller,32UL<<30)==1,"RGB16 memory estimate can reduce the GPU pool size");
        var full=smaller with{HalfStorage=false};
        check(EngineWorkerPolicy.Count(source,full,32UL<<30)==1,"RGB8 with FP32 activations uses the larger memory estimate");
        check(EngineWorkerPolicy.PerEngine(source)==EngineWorkerPolicy.PerEngine(source with{BitDepth=10}),"RGB8 and RGB16 both account for FP32 activation storage");
        check(EngineWorkerPolicy.RequiredGpuBytes(source)>EngineWorkerPolicy.PerEngine(source,true).Gpu,"Device admission and worker counts use the same FP32 footprint");
        check(DevicePolicy.Choose([gpu.Gpu! with{Fp16Storage=true,Fp16Arithmetic=true}]).HalfStorage==false,"Supported FP16 features do not bypass the numerical precision policy");
        check(DevicePolicy.Choose([gpu.Gpu! with{BudgetBytes=4UL<<30}],requiredBytes:EngineWorkerPolicy.RequiredGpuBytes(source)).Device=="cpu","Auto selection excludes a device below the FP32 single-engine estimate");
        check(EngineWorkerPolicy.Count(source,gpu with{HalfStorage=false},32UL<<30,3)==2,"Worker override respects the large-FP32 memory ceiling");
        check(EngineWorkerPolicy.Count(source with{Width=1920,Height=1080},gpu with{HalfStorage=false},32UL<<30)==3,"Smaller FP32 frames can still use three workers within budget");
        var frame=new VideoFrame(12,0,0);frame.Memory.Span.Fill(27);var lease=frame.Retain();frame.Dispose();frame.Dispose();
        check(frame.Memory.Span[0]==27,"A queued lease survives owner disposal");lease.Dispose();lease.Dispose();
        bool released=false;try{frame.Retain();}catch(ObjectDisposedException){released=true;}check(released,"Released frames cannot be retained again");
        int created=0,disposed=0,active=0,peak=0;
        await using(var pool=new EnginePool(3,gpu,_=>{
            int id=created++;return new Fake(async cancel=>{
                if(id==0)throw new IOException("simulated worker failure");
                int n=Interlocked.Increment(ref active);peak=Math.Max(peak,n);
                try{await Task.Delay(10,cancel);return [42];}finally{Interlocked.Decrement(ref active);}
            },()=>disposed++);
        },true)){
            var outputs=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>pool.InterpolateAsync(new byte[1],new byte[1],.5f)));
            check(pool.Count==2&&pool.Choice.Device==gpu.Device&&created==3&&peak==2&&outputs.All(x=>x[0]==42),"One failed worker retries on surviving GPU engines without CPU fallback");
        }
        check(disposed==3&&active==0,"Every failed and healthy engine is disposed after requests drain");
        int cpus=0;
        await using(var pool=new EnginePool(3,gpu,d=>{
            if(d.Device=="cpu"){cpus++;return new Fake(_=>Task.FromResult(new byte[]{7}));}
            return new Fake(_=>throw new IOException("GPU unavailable"));
        },true)){
            var output=await Task.WhenAll(Enumerable.Range(0,9).Select(_=>pool.InterpolateAsync(new byte[1],new byte[1],.5f)));
            check(pool.Count==1&&pool.Choice.Device=="cpu"&&cpus==1&&output.All(x=>x[0]==7),"All failed GPU workers share one CPU fallback and preserve every request");
        }
        await using(var pool=new EnginePool(2,gpu,_=>new Fake(_=>throw new IOException("manual device failed")))){
            bool failed=false;try{await Task.WhenAll(Enumerable.Range(0,4).Select(_=>pool.InterpolateAsync(new byte[1],new byte[1],.5f)));}catch(IOException){failed=true;}
            check(failed&&pool.Count==0,"Explicit GPU failure terminates all waiters without silently switching device");
        }
        using var stop=new CancellationTokenSource();int started=0;
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using(var pool=new EnginePool(3,gpu,_=>new Fake(async cancel=>{
            if(Interlocked.Increment(ref started)==3)ready.SetResult();await Task.Delay(Timeout.Infinite,cancel);return [];
        }))){
            var tasks=Enumerable.Range(0,8).Select(_=>pool.InterpolateAsync(new byte[1],new byte[1],.5f,stop.Token)).ToArray();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));stop.Cancel();bool cancelled=false;
            try{await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));}catch(OperationCanceledException){cancelled=true;}
            check(cancelled&&tasks.All(x=>x.IsCanceled),"Cancellation drains running and queued requests without deadlock");
        }
    }
    public static async Task IntegrationAsync(AppPaths paths,DeviceChoice device,Action<bool,string> check){
        if(device.Device=="cpu")return;
        foreach(string model in new[]{"v4.25","v4.26"})foreach(bool deep in new[]{false,true}){
            byte[] a=new byte[160*90*3*(deep?2:1)],b=new byte[a.Length];
            for(int i=0;i<a.Length;i++){a[i]=(byte)(i%251);b[i]=(byte)((i+17)%251);}
            await using var single=new NativeEngine(paths,model,160,90,device,1,deep);
            float[] times=[.25f,.5f,.75f];var expected=new List<byte[]>();
            foreach(float t in times)expected.Add(await single.InterpolateAsync(a,b,t));
            await using var pool=new EnginePool(3,device,d=>new NativeEngine(paths,model,160,90,d,1,deep));
            var actual=await Task.WhenAll(Enumerable.Range(0,12).Select(i=>pool.InterpolateAsync(a,b,times[i%3])));
            check(actual.Select((x,i)=>x.SequenceEqual(expected[i%3])).All(x=>x),$"Real three-worker outputs match serial bytes at three timesteps: {model}, RGB{(deep?16:8)}");
        }
        if(Environment.GetEnvironmentVariable("FRAMEDOUBLER_REFERENCE_ENGINE") is {} reference){
            // Nonaligned rows above the parallel-conversion threshold, including RGB16.
            foreach(string model in new[]{"v4.25","v4.26"})foreach(bool deep in new[]{false,true}){
                const int width=1282,height=782;
                byte[] a=new byte[width*height*3*(deep?2:1)],b=new byte[a.Length];
                for(int i=0;i<a.Length;i++){a[i]=(byte)(i%251);b[i]=(byte)((i+17)%251);}
                float[] times=[.25f,.5f,.75f];var expected=new List<byte[]>();
                await using(var old=new NativeEngine(paths with{Engine=Path.GetFullPath(reference)},model,width,height,device,1,deep)){
                    foreach(float t in times)expected.Add(await old.InterpolateAsync(a,b,t));
                }
                await using var current=new NativeEngine(paths,model,width,height,device,1,deep);
                bool equal=true;for(int i=0;i<times.Length;i++)equal&=(await current.InterpolateAsync(a,b,times[i])).SequenceEqual(expected[i]);
                check(equal,$"Parallel RGB row conversion matches reference bytes: {model}, RGB{(deep?16:8)}");
            }
        }
    }
}
