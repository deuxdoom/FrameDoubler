using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameDoubler.Core;

public sealed record GpuDevice
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    public int Type { get; init; }
    public uint ApiVersion { get; init; }
    public uint DriverVersion { get; init; }
    public string DriverName { get; init; } = "";
    public ulong LocalHeapBytes { get; init; }
    public ulong? DedicatedBytes { get; init; }
    public ulong? BudgetBytes { get; init; }
    public bool BudgetIsEstimate { get; init; }
    public bool Fp16Storage { get; init; }
    public bool Fp16Arithmetic { get; init; }
    public bool CooperativeMatrix { get; init; }
    public int MaxWorkgroupInvocations { get; init; }
    public double RoughScore { get; init; }
    public double? MeasuredMilliseconds { get; init; }
    public bool? InferencePassed { get; init; }
    public string? Error { get; init; }
    // PCI vendor IDs identify the device only; selection never branches on them.
    public string Manufacturer => VendorId switch { 0x10de => "NVIDIA", 0x1002 => "AMD", 0x8086 => "Intel", _ => $"PCI {VendorId:X4}" };
    public string Kind => Type switch { 0 => "Discrete", 1 => "Integrated", 2 => "Virtual", 3 => "CPU", _ => "Unknown" };
    public bool SupportsWarp => MaxWorkgroupInvocations >= 64;
}

public sealed record HardwareInventory
{
    public string? CpuModel { get; init; }
    public int LogicalProcessors { get; init; }
    public bool VulkanDisabled { get; init; }
    public List<GpuDevice> Gpus { get; init; } = [];
    public static JsonSerializerOptions Json { get; } = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true, WriteIndented = true
    };
}

public sealed record DeviceChoice(string Device, bool HalfStorage, string Explanation, GpuDevice? Gpu)
{
    public string Backend{get;init;}="vulkan";
    public int AccelerationIndex{get;init;}
    public string? RuntimeDirectory{get;init;}
}

public static class DevicePolicy
{
    public static DeviceChoice Choose(IEnumerable<GpuDevice> devices, string requested = "auto", ulong requiredBytes = 0)
    {
        if (requested == "cpu") return new("cpu", false, "CPU 추론: Vulkan보다 처리 속도가 크게 느려질 수 있습니다.", null);
        var list = devices.Where(x => x.SupportsWarp && x.InferencePassed != false).ToList();
        GpuDevice? selected;
        if (requested.StartsWith("gpu:", StringComparison.Ordinal)) {
            if (!int.TryParse(requested.AsSpan(4), out int index)) throw new ArgumentException("Invalid GPU index");
            selected = list.SingleOrDefault(x => x.Index == index) ?? throw new InvalidOperationException("선택한 Vulkan 장치를 실행할 수 없습니다.");
            if (requiredBytes > 0 && selected.BudgetBytes is { } budget && budget < requiredBytes)
                throw new InvalidOperationException("선택한 GPU의 메모리 예산이 부족합니다. 다른 장치나 CPU를 선택하세요.");
        } else if (requested == "auto") {
            selected = list.Where(x => x.BudgetBytes is null || x.BudgetBytes >= requiredBytes)
                .OrderByDescending(x => x.MeasuredMilliseconds is > 0 ? 10000 / x.MeasuredMilliseconds.Value : x.RoughScore)
                .ThenByDescending(x => x.BudgetBytes ?? 0).ThenBy(x => x.Index).FirstOrDefault();
        } else throw new ArgumentException("Device must be auto, gpu:INDEX or cpu");
        return selected is null
            ? new("cpu", false, list.Count>0
                ?"GPU 메모리 예산이 부족해 CPU로 대체합니다. 처리 속도가 크게 느려질 수 있습니다."
                :"사용 가능한 Vulkan GPU가 없어 CPU로 대체합니다. 처리 속도가 크게 느려질 수 있습니다.", null)
            // FP16 activation storage failed the real-frame numerical limits for
            // both RIFE models. Device capability alone cannot establish fidelity.
            : new($"gpu:{selected.Index}", false, $"{selected.Name} / Vulkan / FP32 저장·연산", selected);
    }
}

public static class HardwareDetector
{
    public static async Task<HardwareInventory> DetectAsync(AppPaths paths, bool exercise = true, CancellationToken cancel = default)
    {
        var process = await ProcessRunner.RunAsync(paths.Engine, ["--hardware"], cancel);
        if (process.ExitCode != 0) throw new IOException(process.Error);
        var inventory = JsonSerializer.Deserialize<HardwareInventory>(process.Output, HardwareInventory.Json)
                        ?? throw new IOException("Invalid native hardware response");
        if (!exercise) return inventory;
        var devices = new List<GpuDevice>();
        foreach (var gpu in inventory.Gpus) {
            cancel.ThrowIfCancellationRequested();
            if (!gpu.SupportsWarp) { devices.Add(gpu with { InferencePassed = false, Error = "Required compute size unsupported" }); continue; }
            using var probe=CancellationTokenSource.CreateLinkedTokenSource(cancel);
            probe.CancelAfter(TimeSpan.FromSeconds(30));
            try {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await using var engine = new NativeEngine(paths, "v4.25", 160, 90,
                    new($"gpu:{gpu.Index}", false, "probe", gpu), 1);
                byte[] first = new byte[160*90*3], second = new byte[first.Length];
                for (int i = 0; i < first.Length; ++i) { first[i] = (byte)(i % 251); second[i] = (byte)((i+17) % 251); }
                await engine.InterpolateAsync(first, second, .5f, probe.Token);
                double total = 0;
                for (int i = 0; i < 3; ++i) {
                    watch.Restart();
                    await engine.InterpolateAsync(first, second, .5f, probe.Token);
                    total += watch.Elapsed.TotalMilliseconds;
                }
                devices.Add(gpu with { InferencePassed = true, MeasuredMilliseconds = total / 3 });
            } catch (OperationCanceledException) when(!cancel.IsCancellationRequested) { devices.Add(gpu with{InferencePassed=false,Error="Device inference probe timed out"}); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { devices.Add(gpu with { InferencePassed = false, Error = ex.Message }); }
        }
        return inventory with { Gpus = devices };
    }
}
