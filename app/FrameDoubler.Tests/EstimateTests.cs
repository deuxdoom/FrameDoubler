using FrameDoubler.Core;
using System.Text.Json;

internal static class EstimateTests
{
    public static void Run(Action<bool,string> check){
        foreach(string value in new[]{"N/A","0","-1","bad"}){using var json=JsonDocument.Parse(JsonSerializer.Serialize(new{bit_rate=value}));check(MediaProbe.PositiveBitrate(json.RootElement) is null,"Missing/invalid bitrate stays unknown: "+value);}
        using(var json=JsonDocument.Parse("{\"bit_rate\":\"60000000\"}"))check(MediaProbe.PositiveBitrate(json.RootElement)==60_000_000,"Bitrate stays in bits per second");
        var source=new MediaInfo("",3840,2160,"h264","yuv420p",8,1,60,60,false,3600,[],null,null,null,null,[]){ReportedFrames=216000};
        var analysis=new AnalysisResult(source,24,24,0,30){SampledFrames=48,Intervals=[new(0,new Rational(1,30),false)]};
        var profile=new PerformanceProfile("key","GPU","encoder",.01,.2,.003,DateTime.UtcNow);
        var one=TimeEstimates.Predict(profile,analysis,60,false);var two=TimeEstimates.Predict(profile,analysis with{Source=source with{Duration=7200,ReportedFrames=432000}},60,false);
        check(Math.Abs(two.Seconds-one.Seconds*2)<3,"Two-hour estimates scale by work and fixed startup, not clock wrap");
        check(TimeEstimates.Duration(26*3600).StartsWith("26시간")&&TimeEstimates.Duration(26*3600,true).StartsWith("26 h"),"Long jobs do not wrap at 24 hours");
        check(TimeEstimates.Predict(profile,analysis,60,true).Seconds>one.Seconds,"Verification adds scan work only when selected");
        double scanOne=TimeEstimates.Predict(profile,analysis,60,true).Seconds-one.Seconds;
        double scanTwo=TimeEstimates.Predict(profile,analysis with{Source=source with{Duration=7200,ReportedFrames=432000}},60,true).Seconds-two.Seconds;
        check(Math.Abs(scanOne-scanTwo)<.001,"One and two hours have the same maximum three-minute verification cost");
        check(TimeEstimates.RawSeconds(profile,60,120,59,false)<2+60*.01+120*.003+59*.2,"Decode-ahead cost is not serially added to inference and pipe writes");
        check(TimeEstimates.About(840)=="약 14분"&&TimeEstimates.About(3900)=="약 1시간 05분"&&!TimeEstimates.About(one.Seconds).Contains('~'),"Primary estimate is one approximate duration with two-digit minutes");
        var midnight=new DateTimeOffset(2026,11,14,23,50,0,TimeSpan.FromHours(9));
        check(TimeEstimates.Completion(840,now:midnight)=="완료 예정 11/15 00:04","Completion clock handles next-day jobs");
        check(one.LowerSeconds<one.Seconds&&one.UpperSeconds>one.Seconds,"Short sample estimate exposes uncertainty range");
        check(!new Preferences().Verify,"GUI verification is opt-in");
        string? originalConfig=Environment.GetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR"),originalWork=Environment.GetEnvironmentVariable("FRAMEDOUBLER_WORK_ROOT");
        try{Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",null);Environment.SetEnvironmentVariable("FRAMEDOUBLER_WORK_ROOT",null);
            check(Path.GetFullPath(Preferences.DirectoryPath)==Path.GetFullPath(AppContext.BaseDirectory)&&EncoderDetector.WorkRoot.StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase),"Default data paths are next to exe");
        }finally{Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",originalConfig);Environment.SetEnvironmentVariable("FRAMEDOUBLER_WORK_ROOT",originalWork);}
        string? previous=Environment.GetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR");
        string directory=Path.Combine(EncoderDetector.WorkRoot,"estimate-tests",Guid.NewGuid().ToString("N"));
        try{
            Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",directory);check(TimeEstimates.Save(profile),"Portable performance profile saves");
            TimeEstimates.Observe("key",source.FrameCount,one.OutputFrames,one.Interpolations,false,one.Seconds*1.3);
            check(TimeEstimates.Predict(TimeEstimates.Load("key")!,analysis,60,false).FromHistory,"Completed measurements refine later estimates");
            check(Math.Abs(TimeEstimates.Predict(TimeEstimates.Load("key")!,analysis,60,false).Seconds-one.Seconds*1.3)<.001,"Measured elapsed time calibrates the single estimate without a fixed hardware factor");
            check(TimeEstimates.Load("different-model-or-device") is null,"Other device/settings do not reuse a mismatched profile");
            new Preferences{Verify=true}.Save();check(Preferences.Load().Verify,"Explicit verification choice persists");
            File.WriteAllText(TimeEstimates.ProfilePath,"[null]");check(TimeEstimates.Load("key") is null,"Malformed profile cannot break conversion");
            string install=Path.Combine(directory,"FrameDoubler"),stage=Path.Combine(directory,".FrameDoubler-update-test");Directory.CreateDirectory(install);Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(install,"settings.json"),"settings");File.WriteAllText(Path.Combine(install,"performance.json"),"speeds");File.WriteAllText(Path.Combine(install,"untrusted.exe"),"skip");
            TimeEstimates.PreservePortableFiles(install,stage);
            check(File.ReadAllText(Path.Combine(stage,"settings.json"))=="settings"&&File.ReadAllText(Path.Combine(stage,"performance.json"))=="speeds"&&!File.Exists(Path.Combine(stage,"untrusted.exe")),"Update preserves only portable data, not unlisted executables");
        }finally{Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",previous);}
    }
}
