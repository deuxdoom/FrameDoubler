using System.Diagnostics;

namespace FrameDoubler.Core;

// A persistent, isolated process provides an ABI-independent C++/.NET boundary.
// All frames are binary RGB8; stdout never carries logging or progress strings.
public interface IInterpolationEngine:IAsyncDisposable
{
    Task<byte[]> InterpolateAsync(ReadOnlyMemory<byte> first,ReadOnlyMemory<byte> second,float timestep,CancellationToken cancel=default);
}
public sealed class NativeEngine : IInterpolationEngine
{
    private readonly Process process;
    private readonly Task<string> errors;
    private readonly int bytes;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    public double[] LastTimings { get; private set; } = [];
    public NativeEngine(AppPaths paths, string model, int width, int height, DeviceChoice device, double scale, bool deep=false)
    {
        if (model is not ("v4.25" or "v4.26")) throw new ArgumentException("Unsupported RIFE model");
        if (width < 2 || height < 2 || width > 8192 || height > 8192) throw new ArgumentOutOfRangeException(nameof(width));
        if (scale is not (.5 or 1)) throw new ArgumentOutOfRangeException(nameof(scale));
        bytes = checked(width * height * 3 * (deep?2:1));
        var arguments = new List<string>{paths.Model(model), width.ToString(), height.ToString(),
            device.HalfStorage&&!deep ? "fp16" : "fp32", device.Device, scale.ToString(System.Globalization.CultureInfo.InvariantCulture)};
        if(deep)arguments.Add("rgb16");
        var info = ProcessRunner.StartInfo(paths.Engine, arguments);
        info.RedirectStandardInput = true;
        process = ProcessRunner.Start(info, "native engine");
        errors = process.StandardError.ReadToEndAsync();
    }

    public async Task<byte[]> InterpolateAsync(ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second, float timestep, CancellationToken cancel = default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if (first.Length != bytes || second.Length != bytes) throw new ArgumentException("Incorrect RGB frame size");
        if (!(timestep > 0 && timestep < 1)) throw new ArgumentOutOfRangeException(nameof(timestep));
        await gate.WaitAsync(cancel);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var terminate=deadline.Token.Register(Kill);
        try {
            var input = process.StandardInput.BaseStream;
            byte[] command = new byte[5]; command[0] = (byte)'P';
            BitConverter.TryWriteBytes(command.AsSpan(1), timestep);
            await input.WriteAsync(command, deadline.Token);
            await input.WriteAsync(first, deadline.Token);
            await input.WriteAsync(second, deadline.Token);
            await input.FlushAsync(deadline.Token);
            byte[] timings = new byte[24], frame = new byte[bytes];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(timings, deadline.Token);
            await process.StandardOutput.BaseStream.ReadExactlyAsync(frame, deadline.Token);
            LastTimings = [BitConverter.ToDouble(timings,0), BitConverter.ToDouble(timings,8), BitConverter.ToDouble(timings,16)];
            return frame;
        } catch (OperationCanceledException) {
            Kill();
            if (!cancel.IsCancellationRequested) throw new TimeoutException("Native inference timed out");
            throw;
        } catch (IOException) {
            Kill();
            if(deadline.IsCancellationRequested){cancel.ThrowIfCancellationRequested();throw new TimeoutException("Native inference timed out");}
            string text = await errors;
            throw new IOException("Native inference failed: " + (text.Length > 4000 ? text[^4000..] : text));
        } finally { gate.Release(); }
    }

    private void Kill() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;
        disposed=true;
        try {
            if (!process.HasExited) {
                await process.StandardInput.BaseStream.WriteAsync(new byte[]{(byte)'Q'});
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { Kill(); }
            }
        } catch (IOException) { Kill(); }
        finally { await process.WaitForExitAsync(); process.Dispose(); gate.Dispose(); }
    }
}
