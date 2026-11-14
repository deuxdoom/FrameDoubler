using System.Runtime.InteropServices;

namespace FrameDoubler.Core;

// WDDM's GPU Engine counters, for the exact DXGI adapter used by the live engine.
// Sum process instances per physical engine, then use the busiest engine (Task Manager's
// adapter metric). This is device utilization, not this app's frame-time budget.
public sealed class GpuUsage:IDisposable
{
    private readonly CancellationTokenSource cancel=new();
    private readonly Task worker;
    private readonly object selectionLock=new();
    private string adapter="";
    private double percent=double.NaN;
    private int disposed;
    public double? Percent{get{double value=Volatile.Read(ref percent);return double.IsFinite(value)?value:null;}}
    public void SelectAdapter(string luid){
        lock(selectionLock){
            if(string.Equals(luid,Volatile.Read(ref adapter),StringComparison.OrdinalIgnoreCase))return;
            Volatile.Write(ref percent,double.NaN);Volatile.Write(ref adapter,luid);
        }
    }
    private void Publish(string selected,double? value){lock(selectionLock){if(selected==Volatile.Read(ref adapter))Volatile.Write(ref percent,value??double.NaN);}}
    public GpuUsage(){worker=Task.Run(CollectAsync);}
    private async Task CollectAsync(){
        nint query=0,counter=0;
        try{
            if(!OperatingSystem.IsWindows())return;
            bool sampled=false;
            while(!cancel.IsCancellationRequested){
                string selected=Volatile.Read(ref adapter);
                if(selected.Length==0){Publish(selected,null);sampled=false;}
                else{
                    if(query==0&&PdhOpenQueryW(null,0,out query)==0){
                        if(PdhAddEnglishCounterW(query,@"\GPU Engine(*)\Utilization Percentage",0,out counter)!=0){PdhCloseQuery(query);query=0;}
                    }
                    double? value=null;
                    if(query!=0&&PdhCollectQueryData(query)==0){
                        if(sampled)value=Read(counter,selected);
                        sampled=true;
                    }else sampled=false;
                    // Never publish a late sample from a previously selected adapter.
                    Publish(selected,value);
                }
                await Task.Delay(1000,cancel.Token).ConfigureAwait(false);
            }
        }catch(OperationCanceledException){}
        catch(Exception){Volatile.Write(ref percent,double.NaN);}
        finally{if(query!=0)PdhCloseQuery(query);}
    }
    private static double? Read(nint counter,string luid){
        const uint moreData=0x800007d2,format=0x200|0x8000; // DOUBLE, NOCAP100
        uint bytes=0,count=0;
        if(PdhGetFormattedCounterArrayW(counter,format,ref bytes,ref count,0)!=moreData)return null;
        // The set can change while processes start/stop. Retry with the new required size.
        for(int attempt=0;attempt<3&&bytes>0&&bytes<=64*1024*1024;attempt++){
            nint buffer=Marshal.AllocHGlobal(checked((int)bytes));
            try{
                uint capacity=bytes;
                uint result=PdhGetFormattedCounterArrayW(counter,format,ref bytes,ref count,buffer);
                if(result==moreData)continue;
                if(result!=0||count>capacity/Marshal.SizeOf<Item>())return null;
                var values=new List<(string instance,double value)>();
                int size=Marshal.SizeOf<Item>();
                for(int i=0;i<count;i++){
                    var item=Marshal.PtrToStructure<Item>(buffer+i*size);
                    if(item.Value.Status>1||!double.IsFinite(item.Value.Double))continue;
                    values.Add((Marshal.PtrToStringUni(item.Name)??"",item.Value.Double));
                }
                return Aggregate(luid,values);
            }finally{Marshal.FreeHGlobal(buffer);}
        }
        return null;
    }
    internal static double? Aggregate(string luid,IEnumerable<(string instance,double value)> samples){
        var engines=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
        string marker="_luid_"+luid+"_";
        foreach(var (name,value) in samples){
            if(!double.IsFinite(value))continue;
            int at=name.IndexOf(marker,StringComparison.OrdinalIgnoreCase);
            if(at<0)continue;
            string engine=name[(at+marker.Length)..];
            int type=engine.IndexOf("_engtype_",StringComparison.OrdinalIgnoreCase);
            if(type<0||!engine.StartsWith("phys_",StringComparison.OrdinalIgnoreCase))continue;
            engine=engine[..type];
            engines[engine]=engines.GetValueOrDefault(engine)+Math.Max(0,value);
        }
        return engines.Count==0?null:Math.Clamp(engines.Values.Max(),0,100);
    }
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;cancel.Cancel();_ = worker.ContinueWith(_=>cancel.Dispose(),TaskScheduler.Default);}
    [StructLayout(LayoutKind.Explicit,Size=16)] private struct Value{
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Double;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Item{public nint Name;public Value Value;}
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source,nuint data,out nint query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(nint query,string path,nuint data,out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterArrayW(nint counter,uint format,ref uint bytes,ref uint count,nint items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
