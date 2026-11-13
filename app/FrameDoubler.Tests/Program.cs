using FrameDoubler.Core;
using System.Text.Json;

int passed = 0;
void Assert(bool test, string message) { if (!test) throw new Exception(message); passed++; }
void Throws(Action action, string message) { try { action(); } catch (InvalidOperationException) { passed++; return; } throw new Exception(message); }

GpuDevice Device(int index, uint vendor, double speed, ulong memory, bool half = true) => new() {
    Index=index, VendorId=vendor, Name=$"Test {index}", MaxWorkgroupInvocations=256,
    MeasuredMilliseconds=speed, BudgetBytes=memory, Fp16Storage=half, Fp16Arithmetic=half, InferencePassed=true
};
var slow = Device(0,0x10de,20,8UL<<30);
var fast = Device(1,0x1002,5,4UL<<30,false);
Assert(DevicePolicy.Choose([slow,fast]).Gpu?.Index == 1,"Choose measured performance, not vendor");
Assert(DevicePolicy.Choose([slow,fast],requiredBytes:6UL<<30).Gpu?.Index == 0,"Respect memory budget");
Assert(!DevicePolicy.Choose([fast]).HalfStorage,"FP32 on devices without required FP16 features");
Assert(DevicePolicy.Choose([]).Device == "cpu","No Vulkan device must allow CPU");
Assert(DevicePolicy.Choose([slow],"cpu").Device == "cpu","Explicit CPU");
Assert(DevicePolicy.Choose([slow,fast],"gpu:0").Gpu?.Index == 0,"Manual GPU choice");
Throws(()=>DevicePolicy.Choose([slow],"gpu:9"),"Invalid manual device must fail");
Throws(()=>DevicePolicy.Choose([slow],"gpu:0",12UL<<30),"Manual OOM must fail clearly");
Assert(DevicePolicy.Choose([slow with {InferencePassed=false}]).Device == "cpu","Reject failed inference probe");
Assert(DevicePolicy.Choose([fast with {BudgetBytes=null}]).Gpu is not null,"Unknown memory is not zero memory");
Assert(DevicePolicy.Choose([slow with {VendorId=0x8086}]).Gpu is not null,"Intel eligible");
EncoderCapability sw = new("libopenh264","h264","software",true,false,null);
EncoderCapability hw = new("h264_amf","h264","amf",true,false,null,1);
Assert(EncoderPolicy.Choose([sw,hw],"h264").Encoder == hw,"Prefer tested hardware");
Assert(EncoderPolicy.Choose([sw,hw with {Available=false}],"h264").Encoder == sw,"Software fallback");
Assert(EncoderPolicy.Choose([sw,hw],"h264",hardwareAllowed:false).Encoder == sw,"Software-only path");
Throws(()=>EncoderPolicy.Choose([sw],"hevc"),"No silent codec substitution");
Throws(()=>EncoderPolicy.Choose([sw],"h264",tenBit:true),"No unverified 10-bit encoder");
var parsed = JsonSerializer.Deserialize<HardwareInventory>("{\"cpu_model\":\"AMD test\",\"logical_processors\":8,\"gpus\":[]}",HardwareInventory.Json)!;
Assert(parsed.CpuModel=="AMD test" && parsed.LogicalProcessors==8,"Native schema");
UpdateTests.Parse(Assert);
Assert(default(Rational)==(Rational)0,"Default rational is numeric zero");
Assert(Timeline.DefaultRate(Rational.Parse("30000/1001"))==Rational.Parse("60000/1001"),"Exact NTSC doubling");
Assert((Rational.Parse("600.6")*Rational.Parse("60000/1001")).Ceiling()==36000,"Ten-minute NTSC frame count");
Assert(Rational.Parse("-0.25").Seconds()=="-0.250000000","Exact signed decimal formatting");
var positions=Timeline.Steps(0,new Rational(1,30),60).ToArray();
Assert(positions.Length==2 && positions[0].Position==0 && positions[1].Position==.5f,"30 to 60 midpoint timeline");
var cadence=new CadenceDetector();var labels=new List<FrameLabel>();
foreach(float value in new float[]{0,0,20,20,40,40,60,60}){
    var label=cadence.Push(new Signature(32,32,Enumerable.Repeat(value,1024).ToArray()));if(label.HasValue)labels.Add(label.Value);
}
labels.Add(cadence.Flush()!.Value);
Assert(labels.Count(x=>x==FrameLabel.Unique)==4 && labels.Count(x=>x==FrameLabel.Duplicate)==4,"Alternating duplicate cadence");
cadence=new CadenceDetector();labels.Clear();
for(int i=0;i<10;i++){var label=cadence.Push(new Signature(32,32,new float[1024]));if(label.HasValue)labels.Add(label.Value);}
labels.Add(cadence.Flush()!.Value);
Assert(labels.Count(x=>x==FrameLabel.Static)==5,"Long static holds stop interpolation");
var black=new Signature(32,32,new float[1024]).Descriptor();var white=new Signature(32,32,Enumerable.Repeat(255f,1024).ToArray()).Descriptor();
Assert(Signature.CutScore(black,white)>.99 && Signature.CutScore(black,black)==0,"Histogram cuts");
string? previousConfig=Environment.GetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR");
try{
    string config=Path.Combine(EncoderDetector.WorkRoot,"settings-tests",Guid.NewGuid().ToString("N"));
    Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",config);
    new Preferences{Language="en",OutputDirectory="F:\\한글 출력",Preset="quality",Fps="60000/1001"}.Save();
    var loaded=Preferences.Load();Assert(loaded.Language=="en"&&loaded.OutputDirectory=="F:\\한글 출력"&&loaded.Fps=="60000/1001","Settings restart/Unicode round trip");
    File.WriteAllText(Path.Combine(config,"settings.json"),"invalid json");
    Assert(Preferences.Load().Language=="ko","Malformed preferences recover safely");
}finally{Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",previousConfig);}
var hudInfo=new MediaInfo("",2,2,"","rgb24",8,1,30,30,false,1,[0],null,null,null,null,[]);
using(var a=new VideoFrame(12,0,0))using(var b=new VideoFrame(12,1,1)){
    Array.Fill(a.Buffer,(byte)10,0,12);Array.Fill(b.Buffer,(byte)10,0,12);b.Buffer[3]=20;
    byte[] middle=Enumerable.Repeat((byte)50,12).ToArray();new HudRegion(0,0,2,1).Restore(a,b,middle,hudInfo);
    Assert(middle[0]==10&&middle[3]==50&&middle[6]==50,"HUD restores only identical source pixels inside the selected region");
}

if (args.Contains("--integration")) {
    var paths = AppPaths.Discover();
    var detected = await HardwareDetector.DetectAsync(paths);
    Assert(detected.LogicalProcessors>0,"CPU inventory");
    Assert(detected.Gpus.All(x=>x.InferencePassed==true),"Available real GPU probes");
    byte[] first = new byte[160*90*3], second = new byte[first.Length];
    for(int i=0;i<first.Length;i++){first[i]=(byte)(i%251);second[i]=(byte)((i+5)%251);}
    await using(var cpu = new NativeEngine(paths,"v4.25",160,90,DevicePolicy.Choose([],"cpu"),1)) {
        var frame=await cpu.InterpolateAsync(first,second,.5f);
        Assert(frame.Length==first.Length && !frame.SequenceEqual(first),"Actual CPU inference");
    }
    var codecs=await EncoderDetector.DetectAsync(paths);
    Assert(codecs.Any(x=>x.Available&&!x.Hardware&&x.Codec=="h264"),"Actual software H264 fallback");
    string unicodeModels=Path.Combine(EncoderDetector.WorkRoot,"한글 모델");string unicodeVersion=Path.Combine(unicodeModels,"v4.25");Directory.CreateDirectory(unicodeVersion);
    foreach(string name in new[]{"rife.ncnn.param","rife.ncnn.bin"})File.Copy(Path.Combine(paths.Model("v4.25"),name),Path.Combine(unicodeVersion,name),true);
    await using(var unicode=new NativeEngine(paths with{Models=unicodeModels},"v4.25",160,90,DevicePolicy.Choose([],"cpu"),1)){
        Assert((await unicode.InterpolateAsync(first,second,.5f)).Length==first.Length,"Native Unicode model directory");
    }
    Directory.CreateDirectory(EncoderDetector.WorkRoot);
    await File.WriteAllTextAsync(Path.Combine(EncoderDetector.WorkRoot,"hardware-verification.json"),
        JsonSerializer.Serialize(new{detected,codecs},new JsonSerializerOptions{WriteIndented=true}));
    string? previous=Environment.GetEnvironmentVariable("FRAMEDOUBLER_DISABLE_VULKAN");
    try{
        Environment.SetEnvironmentVariable("FRAMEDOUBLER_DISABLE_VULKAN","1");
        var disabled=await HardwareDetector.DetectAsync(paths);
        Assert(disabled.Gpus.Count==0 && DevicePolicy.Choose(disabled.Gpus).Device=="cpu","Forced Vulkan-off fallback");
    }finally{Environment.SetEnvironmentVariable("FRAMEDOUBLER_DISABLE_VULKAN",previous);}
}
if(args.Contains("--pipeline"))await PipelineTests.RunAsync(AppPaths.Discover(),Assert);
EstimateTests.Run(Assert);
if(args.Contains("--long"))await PipelineTests.LongAsync(AppPaths.Discover(),Assert);
int packageArgument=Array.IndexOf(args,"--update-package");
if(packageArgument>=0)await UpdateTests.PackageAsync(args[packageArgument+1],Assert);
Console.WriteLine($"PASS {passed} assertions. Simulated devices are policy tests, not actual hardware qualification.");
