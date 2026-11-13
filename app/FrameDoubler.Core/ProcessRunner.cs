using System.Diagnostics;

namespace FrameDoubler.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class ProcessRunner
{
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancel = default, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = StartInfo(executable, arguments) };
        if (!process.Start()) throw new IOException($"Cannot start {executable}");
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try {
            await process.WaitForExitAsync(limit.Token);
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

public sealed record AppPaths(string Engine, string Models, string FFmpeg, string FFprobe)
{
    public string Model(string version) => Path.Combine(Models, version);
    public static AppPaths Discover()
    {
        string? data = Environment.GetEnvironmentVariable("FRAMEDOUBLER_DATA");
        string root = string.IsNullOrWhiteSpace(data) ? AppContext.BaseDirectory : Path.GetFullPath(data);
        return new(Path.Combine(root, "native", "rife-poc.exe"), Path.Combine(root, "models"),
                   Path.Combine(root, "ffmpeg", "ffmpeg.exe"), Path.Combine(root, "ffmpeg", "ffprobe.exe"));
    }
}
