using FrameDoubler.Core;

internal static class GpuUsageTests
{
    public static void Run(Action<bool,string> assert){
        const string adapter="0x00000000_0x00001234";
        var samples=new (string,double)[]{
            ($"pid_1_luid_{adapter}_phys_0_eng_0_engtype_3D",20),
            ($"pid_2_luid_{adapter}_phys_0_eng_0_engtype_3D",35),
            ($"pid_1_luid_{adapter}_phys_0_eng_1_engtype_Compute",80),
            ("pid_3_luid_0x00000000_0x00009999_phys_0_eng_0_engtype_3D",100),
            ($"pid_4_luid_{adapter}_phys_0_eng_2_engtype_Copy",double.NaN)};
        assert(GpuUsage.Aggregate(adapter,samples)==80,"GPU load uses the busiest engine of the selected adapter, not the sum of different engines or GPUs");
        assert(GpuUsage.Aggregate(adapter,samples.Take(2))==55,"GPU engine load sums its valid process instances");
        assert(GpuUsage.Aggregate(adapter,[(samples[0].Item1,120)])==100&&GpuUsage.Aggregate(adapter,[(samples[0].Item1,-1)])==0,"GPU utilization remains in the physical 0-100 percent range");
        assert(GpuUsage.Aggregate(adapter,[]) is null&&GpuUsage.Aggregate("0x00000000_0xffffffff",samples) is null,
            "Unavailable GPU counters stay unknown instead of reporting zero or another adapter's load");
        var status=LiveStatus.Parse("{\"gpuLuid\":\""+adapter+"\",\"stages\":{},\"totals\":{},\"recording\":{}}");
        assert(status.GpuLuid==adapter,"Live status identifies the actual DXGI adapter for GPU utilization");
    }
}
