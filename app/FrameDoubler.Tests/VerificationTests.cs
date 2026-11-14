using FrameDoubler.Core;
using System.Text.Json;

internal static class VerificationTests
{
    public static void Unit(Action<bool,string> check){
        foreach(int seconds in new[]{1,21,60,90,120,180}){
            var windows=VerificationWindows.Plan(seconds);
            check(windows.Length==1&&windows[0].Start==0&&windows[0].End==seconds,"Overlapping sample windows merge: "+seconds);
        }
        var longWindows=VerificationWindows.Plan(7200);
        check(longWindows.Select(x=>x.Start).SequenceEqual(new Rational[]{0,3570,7140})&&longWindows.All(x=>x.Duration==60),"Two hours scan only start, centered middle and end minutes");
        var fractional=VerificationWindows.Plan(Rational.Parse("600.6"));
        check(fractional[1].Start==Rational.Parse("270.3")&&fractional[2].End==Rational.Parse("600.6")&&VerificationWindows.Seconds(7200)==180,"Exact fractional windows and capped scan cost");
    }
    public static async Task IntegrationAsync(AppPaths paths,Action<bool,string> check){
        string root=Path.Combine(EncoderDetector.WorkRoot,"sample-verifier",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        async Task FF(params string[] args){var r=await ProcessRunner.RunAsync(paths.FFmpeg,new[]{"-v","error","-y"}.Concat(args),timeout:TimeSpan.FromMinutes(3));if(r.ExitCode!=0)throw new IOException(r.Error);}
        string source=Path.Combine(root,"420.mp4"),gap=Path.Combine(root,"gap-repeat.mp4"),bad=Path.Combine(root,"repeat.mp4");
        await FF("-f","lavfi","-i","testsrc2=s=160x90:r=60:d=420","-f","lavfi","-i","sine=sample_rate=48000:duration=420",
            "-c:v","libopenh264","-b:v","2M","-c:a","aac","-shortest",source);
        var original=await Verifier.VerifyAsync(paths,source,source,60);
        check(original.Ok&&original.Partial&&original.Windows.Length==3&&original.InspectedSeconds==180&&original.InspectedFrames==10800,"420s positive control scans exactly 180s / 10800 frames with sampled audio");
        string shifted=Path.Combine(root,"offset.mp4");
        await FF("-i",source,"-c","copy","-output_ts_offset","10.125",shifted);
        var offset=await Verifier.VerifyAsync(paths,shifted,shifted,60);
        check(offset.Ok&&offset.InspectedFrames==10800,"Bounded seeks handle nonzero fractional stream start timestamps");
        await FF("-i",source,"-vf","select='not(between(t,70,90)*mod(n,2))',fps=60","-c:v","libopenh264","-b:v","2M","-c:a","copy",gap);
        var skipped=await Verifier.VerifyAsync(paths,source,gap,60);
        check(skipped.Ok&&skipped.Partial&&skipped.InspectedFrames==10800,"An uninspected gap is explicitly outside verification coverage");
        await FF("-i",source,"-vf","framestep=2,fps=60","-c:v","libopenh264","-b:v","2M","-c:a","copy",bad);
        var rejected=await Verifier.VerifyAsync(paths,source,bad,60);
        check(!rejected.Ok&&rejected.EvaluatedDuplicateRatio>.01,"Sample windows still reject repeated moving frames without lowering the 1% criterion");
        await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{original,offset,skipped,rejected},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Sample verification artifacts: "+root);
    }
}
