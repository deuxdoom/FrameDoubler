using System.Text.Json;
using System.Text.RegularExpressions;

namespace FrameDoubler.Core;

public sealed record EncoderCapability(string Name, string Codec, string Backend, bool Available,
    bool TenBit, string? Reason, double? ProbeMilliseconds = null)
{
    public bool Hardware => Backend != "software";
}
public sealed record EncoderChoice(EncoderCapability Encoder, string Explanation);

public static class EncoderDetector
{
    public static string WorkRoot => Environment.GetEnvironmentVariable("FRAMEDOUBLER_WORK_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameDoubler", "work");

    public static async Task<List<EncoderCapability>> DetectAsync(AppPaths paths, CancellationToken cancel = default)
    {
        var listing = await ProcessRunner.RunAsync(paths.FFmpeg, ["-hide_banner", "-encoders"], cancel);
        if (listing.ExitCode != 0) throw new IOException("Cannot enumerate FFmpeg encoders: " + listing.Error);
        var available = Regex.Matches(listing.Output, @"^\s*V\S{5}\s+(\S+)", RegexOptions.Multiline)
                             .Select(x => x.Groups[1].Value).ToHashSet();
        var result = new List<EncoderCapability>();
        foreach (string codec in new[]{"h264", "hevc", "av1"}) {
            var candidates = new[]{(codec+"_nvenc", "nvenc"), (codec+"_amf", "amf"), (codec+"_qsv", "qsv"),
                (codec switch { "h264" => "libopenh264", "hevc" => "libkvazaar", _ => "libsvtav1" }, "software")};
            foreach (var (name, backend) in candidates) {
                cancel.ThrowIfCancellationRequested();
                if (!available.Contains(name)) { result.Add(new(name,codec,backend,false,false,"Not included in this FFmpeg build")); continue; }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var eight = await ProbeAsync(paths, name, false, cancel);
                // Check produced bit depth, not merely FFmpeg exit status: encoders may
                // silently convert unsupported 10-bit input back to 8-bit.
                bool ten = eight.Success && (await ProbeAsync(paths, name, true, cancel)).Success;
                result.Add(new(name,codec,backend,eight.Success,ten,eight.Reason,watch.Elapsed.TotalMilliseconds));
            }
        }
        return result;
    }

    private static async Task<(bool Success, string? Reason)> ProbeAsync(AppPaths paths, string name, bool deep, CancellationToken cancel)
    {
        string folder = Path.Combine(WorkRoot, "encoder-probes");
        Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, $"{Guid.NewGuid():N}.mkv");
        try {
            var args = new List<string>{"-v","error","-y","-f","lavfi","-i","color=c=gray:s=640x360:r=30",
                "-frames:v","2","-an","-c:v",name,"-threads","2","-pix_fmt",deep?"yuv420p10le":"yuv420p","-b:v","1M"};
            if (name == "libsvtav1") args.AddRange(["-preset","10","-svtav1-params","lp=2"]);
            args.Add(output);
            var check = await ProcessRunner.RunAsync(paths.FFmpeg, args, cancel, TimeSpan.FromSeconds(15));
            if (check.ExitCode != 0) return (false, Tail(check.Error));
            var probe = await ProcessRunner.RunAsync(paths.FFprobe,
                ["-v","error","-select_streams","v:0","-show_entries","stream=pix_fmt","-of","json",output], cancel);
            if (probe.ExitCode != 0) return (false,Tail(probe.Error));
            using var json = JsonDocument.Parse(probe.Output);
            string pixel = json.RootElement.GetProperty("streams")[0].GetProperty("pix_fmt").GetString() ?? "";
            bool actualDeep = pixel.Contains("10",StringComparison.Ordinal) || pixel.StartsWith("p010",StringComparison.Ordinal);
            return actualDeep == deep ? (true,null) : (false,$"Produced {pixel} instead of requested bit depth");
        } catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false,ex.Message); }
        finally { try { File.Delete(output); } catch (IOException) { } }
    }
    private static string Tail(string value) => value.Length > 1000 ? value[^1000..] : value;
}

public static class EncoderPolicy
{
    public static EncoderChoice Choose(IEnumerable<EncoderCapability> encoders, string codec,
        string requested = "auto", bool tenBit = false, bool hardwareAllowed = true)
    {
        if (codec is not ("h264" or "hevc" or "av1")) throw new ArgumentException("Unsupported codec");
        var possible = encoders.Where(x => x.Available && x.Codec == codec && (!tenBit || x.TenBit)
                                          && (hardwareAllowed || !x.Hardware)).ToList();
        EncoderCapability? chosen;
        if (requested != "auto") {
            chosen = possible.FirstOrDefault(x => x.Name == requested || x.Backend == requested)
                     ?? throw new InvalidOperationException($"{requested} / {codec} is unavailable for this bit depth");
        } else {
            // Runtime success is mandatory. Prefer hardware, then measured probe time;
            // no inference GPU brand determines whether an encoder is permitted.
            chosen = possible.OrderByDescending(x => x.Hardware).ThenBy(x => x.ProbeMilliseconds ?? double.MaxValue).FirstOrDefault();
        }
        if (chosen is null) throw new InvalidOperationException($"No tested {codec} encoder is available. Select another codec.");
        return new(chosen, chosen.Hardware ? $"인코더: {chosen.Name} ({codec})"
            : $"소프트웨어 인코더: {chosen.Name} ({codec}). 하드웨어 인코딩보다 느릴 수 있습니다.");
    }

    public static IEnumerable<string> Arguments(EncoderCapability encoder, int quality, string? bitrate = null)
    {
        if (quality is < 1 or > 51) throw new ArgumentOutOfRangeException(nameof(quality));
        var args = new List<string>{"-c:v",encoder.Name};
        if (bitrate is not null) { args.AddRange(["-b:v",bitrate]); return args; }
        args.AddRange(encoder.Backend switch {
            "nvenc" => ["-preset","p5","-rc","vbr","-cq",quality.ToString(),"-b:v","0"],
            "amf" => ["-rc","cqp","-qp_i",quality.ToString(),"-qp_p",quality.ToString()],
            "qsv" => ["-global_quality",quality.ToString()],
            _ => encoder.Name switch {
                "libsvtav1" => ["-preset","8","-crf",quality.ToString(),"-svtav1-params","lp=4"],
                "libkvazaar" => ["-kvazaar-params",$"qp={quality}:threads=4"],
                _ => new[]{"-b:v","12000000"}
            }
        });
        return args;
    }
}
