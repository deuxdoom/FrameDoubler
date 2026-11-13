using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FrameDoubler.Core;

public sealed record PerformanceProfile(string Key,string Device,string Encoder,double DecodePerFrame,double InferencePerCall,double EncodePerFrame,DateTime MeasuredUtc){
    public double[] WithoutVerification{get;init;}=[];
    public double[] WithVerification{get;init;}=[];
}
public sealed record TimeEstimate(double Seconds,double LowerSeconds,double UpperSeconds,long OutputFrames,long Interpolations,
    bool Verification,bool FromHistory,string Device,string Encoder,string ProfileKey);

public static class TimeEstimates
{
    private static readonly object gate=new();
    public static string ProfilePath=>Path.Combine(Preferences.DirectoryPath,"performance.json");
    public static double Median(IEnumerable<double> values){var a=values.Order().ToArray();return a.Length==0?0:a[a.Length/2];}
    public static PerformanceProfile? Load(string key){
        lock(gate){try{
            if(!File.Exists(ProfilePath))return null;
            return JsonSerializer.Deserialize<PerformanceProfile[]>(File.ReadAllText(ProfilePath))?
                .FirstOrDefault(x=>Valid(x)&&x.Key==key&&x.MeasuredUtc>DateTime.UtcNow.AddDays(-7));
        }catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){return null;}}
    }
    public static bool Save(PerformanceProfile profile){
        if(!Valid(profile))return false;
        lock(gate){string? temporary=null;try{
            List<PerformanceProfile> entries=[];
            if(File.Exists(ProfilePath))try{entries=JsonSerializer.Deserialize<List<PerformanceProfile>>(File.ReadAllText(ProfilePath))??[];}catch(JsonException){}
            entries.RemoveAll(x=>!Valid(x)||x.Key==profile.Key);entries.Add(profile);
            Directory.CreateDirectory(Preferences.DirectoryPath);temporary=ProfilePath+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(entries.OrderByDescending(x=>x.MeasuredUtc).Take(32)));
            File.Move(temporary,ProfilePath,true);return true;
        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return false;}
        finally{if(temporary is not null&&File.Exists(temporary))File.Delete(temporary);}}
    }
    private static bool Valid(PerformanceProfile? x)=>x is not null&&x.WithVerification is not null&&x.WithoutVerification is not null&&x.DecodePerFrame>0&&double.IsFinite(x.DecodePerFrame)&&x.EncodePerFrame>0&&double.IsFinite(x.EncodePerFrame)
        &&x.InferencePerCall>=0&&double.IsFinite(x.InferencePerCall)&&x.WithVerification.Concat(x.WithoutVerification).All(v=>v>0&&double.IsFinite(v));
    public static string Key(params object?[] values)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
    public static double RawSeconds(PerformanceProfile p,long inputFrames,long outputFrames,long interpolations,bool verify)=>
        2+inputFrames*p.DecodePerFrame+outputFrames*p.EncodePerFrame+interpolations*p.InferencePerCall+
        (verify?(inputFrames+outputFrames)*p.DecodePerFrame:0);
    public static TimeEstimate Predict(PerformanceProfile p,AnalysisResult analysis,Rational fps,bool verify){
        long output=(analysis.Source.Duration*fps).Ceiling();double seconds=analysis.Intervals.Sum(x=>(x.Second-x.First).ToDouble());
        long sampled=analysis.Intervals.Where(x=>!x.Hold).Sum(x=>Timeline.Steps(x.First,x.Second,fps).LongCount(y=>y.Position>0&&y.Position<1));
        long interpolations=seconds>0?(long)Math.Round(sampled/seconds*analysis.Source.Duration.ToDouble()):0;
        interpolations=Math.Clamp(interpolations,0,output);
        var history=verify?p.WithVerification:p.WithoutVerification;
        double factor=history.Length>0?Median(history):1;
        double estimate=RawSeconds(p,analysis.Source.FrameCount,output,interpolations,verify)*factor;
        if(!double.IsFinite(estimate)||estimate<=0)throw new IOException("Cannot calculate a finite conversion estimate");
        return new(estimate,estimate*(history.Length>0?.8:.7),estimate*(history.Length>0?1.4:1.7),output,interpolations,verify,history.Length>0,p.Device,p.Encoder,p.Key);
    }
    public static void Observe(string key,long inputFrames,long outputFrames,long interpolations,bool verify,double seconds){
        var p=Load(key);if(p is null||seconds<3)return;
        double factor=seconds/RawSeconds(p,inputFrames,outputFrames,interpolations,verify);
        if(!double.IsFinite(factor)||factor<=0)return;
        double[] updated=(verify?p.WithVerification:p.WithoutVerification).Append(factor).TakeLast(5).ToArray();
        Save(verify?p with{WithVerification=updated}:p with{WithoutVerification=updated});
    }
    public static string Duration(double seconds,bool english=false){
        if(!double.IsFinite(seconds)||seconds<0)return english?"Unknown":"계산 불가";
        long minutes=(long)Math.Ceiling(seconds/60);
        if(minutes<1)return english?"<1 min":"1분 미만";
        long hours=minutes/60,remaining=minutes%60;
        return hours==0?(english?$"{minutes} min":$"{minutes}분"):
            (english?$"{hours} h"+(remaining>0?$" {remaining} min":""):$"{hours}시간"+(remaining>0?$" {remaining}분":""));
    }
    public static string Range(TimeEstimate e,bool english=false)=>$"{(english?"About ":"약 ")}{Duration(e.LowerSeconds,english)} ~ {Duration(e.UpperSeconds,english)}";
    public static void PreservePortableFiles(string installation,string stage){
        installation=Path.GetFullPath(installation);stage=Path.GetFullPath(stage);
        if(Path.GetDirectoryName(installation)!=Path.GetDirectoryName(stage)||!Path.GetFileName(stage).StartsWith(".FrameDoubler-update-",StringComparison.Ordinal))throw new IOException("Invalid portable update stage");
        foreach(string name in new[]{"settings.json","performance.json"}){
            string source=Path.Combine(installation,name);if(File.Exists(source))File.Copy(source,Path.Combine(stage,name),true);
        }
    }
}
