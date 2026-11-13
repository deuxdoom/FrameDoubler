using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FrameDoubler.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class ProcessRunner
{
    // FFmpeg and ffprobe always write UTF-8. Without an explicit encoding a GUI process decodes
    // with the ANSI code page, which corrupts non-ASCII paths/tags inside ffprobe JSON.
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    public static Process Start(ProcessStartInfo info, string description)
    {
        var process = Process.Start(info) ?? throw new IOException("Cannot start " + description);
        ChildProcesses.Track(process);
        return process;
    }

    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancel = default, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        using var process = Start(StartInfo(executable, arguments), executable);
        using var terminate=limit.Token.Register(()=>{try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(InvalidOperationException){} });
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try {
            await process.WaitForExitAsync(limit.Token);
            limit.Token.ThrowIfCancellationRequested();
            return new(process.ExitCode, await output, await error);
        } catch {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (!cancel.IsCancellationRequested && limit.IsCancellationRequested)
                throw new TimeoutException($"{Path.GetFileName(executable)} timed out");
            throw;
        }
    }
}

// Helper processes (FFmpeg, ffprobe, the RIFE engine) join one job object that Windows closes
// when FrameDoubler exits for any reason, including a crash, so none of them can be left running.
// The update installer is started separately and is intentionally not tracked.
public static class ChildProcesses
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimit info, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(nint process, SafeFileHandle job, out bool result);
    private static readonly SafeFileHandle? Job = Create();
    private static SafeFileHandle? Create()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var job = CreateJobObject(0, null);
        if (job.IsInvalid) return null;
        var limit = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
        if (SetInformationJobObject(job, ExtendedLimitInformation, ref limit, Marshal.SizeOf<ExtendedLimit>())) return job;
        job.Dispose();
        return null;
    }
    public static bool Available => Job is not null;
    public static void Track(Process process)
    {
        if (Job is null) return;
        try { AssignProcessToJobObject(Job, process.Handle); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    public static bool IsTracked(Process process) => Job is not null && IsProcessInJob(process.Handle, Job, out bool inside) && inside;
}

public sealed record AppPaths(string Engine, string Models, string FFmpeg, string FFprobe)
{
    public string Model(string version) => Path.Combine(Models, version);
    public static AppPaths Discover()
    {
        string? data = Environment.GetEnvironmentVariable("FRAMEDOUBLER_DATA");
        string root = string.IsNullOrWhiteSpace(data) ? AppContext.BaseDirectory : Path.GetFullPath(data);
        return new(Path.Combine(root, "engine", "rife-poc.exe"), Path.Combine(root, "models"),
                   Path.Combine(root, "ffmpeg", "ffmpeg.exe"), Path.Combine(root, "ffmpeg", "ffprobe.exe"));
    }
}
