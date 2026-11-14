using FrameDoubler.Core;
using System.Text.Json;

// A copy of this program stands in for FrameDoubler.exe in the installer tests.
if (InstallerTests.FakeApp(args)) return;
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
PreferenceTests.Run(Assert);
var hudInfo=new MediaInfo("",2,2,"","rgb24",8,1,30,30,false,1,[0],null,null,null,null,[]);
using(var a=new VideoFrame(12,0,0))using(var b=new VideoFrame(12,1,1)){
    Array.Fill(a.Buffer,(byte)10,0,12);Array.Fill(b.Buffer,(byte)10,0,12);b.Buffer[3]=20;
    byte[] middle=Enumerable.Repeat((byte)50,12).ToArray();new HudRegion(0,0,2,1).Restore(a,b,middle,hudInfo);
    Assert(middle[0]==10&&middle[3]==50&&middle[6]==50,"HUD restores only identical source pixels inside the selected region");
}

{
    var start=ProcessRunner.StartInfo("ffprobe.exe",["-version"]);
    Assert(start.StandardOutputEncoding?.WebName=="utf-8"&&start.StandardErrorEncoding?.WebName=="utf-8","Helper output is decoded as UTF-8 (non-ASCII paths in ffprobe JSON)");
    string hlg=ToneMap.Filter(new HdrSource("arib-std-b67","bt2020","bt2020nc","tv",10));
    Assert(hlg.StartsWith("zscale=tin=arib-std-b67:pin=bt2020:min=bt2020nc:rin=tv:t=linear:npl=100,",StringComparison.Ordinal)&&hlg.Contains("tonemap=tonemap=hable:desat=0")&&hlg.EndsWith("zscale=t=bt709,format=gbrp",StringComparison.Ordinal),"HLG tone-map chain to BT.709");
    string pq=ToneMap.Filter(new HdrSource("smpte2084",null,"unknown",null,10));
    Assert(pq.StartsWith("zscale=tin=smpte2084:pin=bt2020:min=bt2020nc:rin=tv:",StringComparison.Ordinal)&&new HdrSource("smpte2084",null,null,null,10).Name=="PQ","PQ with missing tags falls back to BT.2020 limited range");
    string body="## 🚀 What's New in 1.0.1\n\n요약 문장입니다.\n\n---\n\n### ✨ 새로운 기능\n\n- **HDR**: `SDR`로 변환합니다.\n- [링크](https://example.com) 설명\n\n### 🔧 수정\n\n- 한글 경로\n\n---\n\n### 📦 설치 방법\n\n1. 압축을 풉니다.\n\n### 🔐 파일 검증\n\n| 파일 | SHA |\n| --- | --- |\n| `FrameDoubler.zip` | `ABC` |\n\n```powershell\nGet-FileHash FrameDoubler.zip\n```\n\n---\n\n**🔍 전체 변경 기록**: [CHANGELOG.md](https://example.com)\n";
    string readable=Updates.ReadableNotes(body);
    Assert(readable==string.Join(Environment.NewLine,"요약 문장입니다.","","✨ 새로운 기능","• HDR: SDR로 변환합니다.","• 링크 설명","","🔧 수정","• 한글 경로"),"Release notes show only the change lists without Markdown syntax");
    var sdr=new MediaInfo("",2,2,"","yuv420p",8,1,30,30,false,1,[0],"bt709","tv","bt709","bt709",[]);
    Assert(!sdr.Hdr&&ToneMap.Arguments(sdr).Length==0&&ToneMap.Arguments(sdr with{HdrInput=new("arib-std-b67","bt2020","bt2020nc","tv",10)})[0]=="-vf","Only HDR input adds the tone-map filter");
}

{
    Assert(OutputPresets.BuiltIns.Select(x=>x.Id).SequenceEqual(new[]{"source","source-scaled","youtube-2160","youtube-1080"}),"Only the four requested presets are offered");
    var src=new MediaInfo("",720,480,"h264","yuv420p",8,1,30,30,false,1,[0],null,null,null,null,[]){VideoBitrate=3_219_186};
    var h264=new EncoderCapability("h264_nvenc","h264","nvenc",true,false,null);
    var unchanged=OutputPresets.Apply(OutputPresets.Find("source"),new ConversionOptions());
    var scaled=OutputPresets.Apply(OutputPresets.Find("source-scaled"),new ConversionOptions());
    Assert(unchanged.MatchSource&&!unchanged.ScaleSourceBitrate&&unchanged.Platform is null&&unchanged.OutputHeight is null,"Default preserves source size, codec and audio passthrough");
    Assert(OutputPresets.Encoding(src,unchanged,[h264],60)==("h264","3219186")&&OutputPresets.Encoding(src,unchanged,[h264],120).Bitrate=="3219186","Source bitrate stays exact at 60 and 120fps; never platform 12M");
    Assert(OutputPresets.Encoding(src,scaled,[h264],60).Bitrate=="6438372"&&OutputPresets.Encoding(src,scaled,[h264],120).Bitrate=="12876744"&&scaled.Platform is null,"Optional source multiplier follows fps with audio passthrough and no 2x cap");
    Assert(OutputPresets.SourceBitrate(src with{Fps=60},60,true)=="3219186","Already-60fps game capture does not double the file bitrate");
    Assert(OutputPresets.SourceBitrate(src with{VideoBitrate=null,OverallBitrate=null},60) is null,"Unknown bitrate has an explicit quality fallback");
    foreach(var id in new[]{"youtube-2160","youtube-1080"}){
        var opt=OutputPresets.Apply(OutputPresets.Find(id),new ConversionOptions());
        var encoded=OutputPresets.Encoding(src,opt,[h264],60);
        Assert(opt.Platform=="youtube"&&opt.Codec=="h264"&&!opt.QualityOnly&&encoded.Bitrate==(id=="youtube-2160"?"45M":"10M"),"YouTube uses requested fixed bitrate, H264 and AAC: "+id);
    }
    Assert(OutputPresets.OutputSize(3840,2160,1080)==(1920,1080)&&OutputPresets.OutputSize(720,480,null)==(720,480),"Source size or proportional preset resize");
    Assert(OutputPresets.SourceCodec(src,[h264],true)=="h264"&&OutputPresets.SourceCodec(src with{Codec="vp9"},[h264],true) is null,"Keep source codec only when encodable here");
    Assert(OutputPresets.ParseBitrate("3219186")==3_219_186&&OutputPresets.ParseBitrate("12M")==12_000_000&&OutputPresets.FindExact("quality") is null,"Removed presets cannot silently select a different bitrate");
    var audio=new AudioInfo(0,"aac",2,48000,0,1,new Rational(1,48000));
    Assert(OutputPresets.AudioBitrate(audio with{Bitrate=256000})==256000&&OutputPresets.AudioBitrate(audio with{Bitrate=384000})==256000&&OutputPresets.AudioBitrate(audio with{Bitrate=255999})==192000&&OutputPresets.AudioBitrate(audio with{Bitrate=128000})==192000&&OutputPresets.AudioBitrate(audio)==192000,"Per-track YouTube audio uses 256k or 192k, including boundary and unknown bitrate");
    var hq=EncoderPolicy.Arguments(h264 with{HighQualityTuning=true},19,"12M").ToArray();
    Assert(hq.Contains("p7")&&hq.Contains("fullres")&&!EncoderPolicy.Arguments(h264,19,"12M").Contains("-rc-lookahead")&&!EncoderPolicy.Arguments(hw,19,"12M").Contains("-rc-lookahead"),"Optional NVENC quality tuning is isolated from base and other backends");
}
VerificationTests.Unit(Assert);
if (args.Contains("--integration")) {
    var paths = AppPaths.Discover();
    using(var helper=ProcessRunner.Start(ProcessRunner.StartInfo(paths.FFmpeg,["-nostdin","-v","error","-f","lavfi","-i","nullsrc=s=16x16","-f","null","-"]),"job test")){
        try{Assert(ChildProcesses.Available&&ChildProcesses.IsTracked(helper),"Helper processes join the kill-on-close job");}
        finally{helper.Kill(true);await helper.WaitForExitAsync();}
    }
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
if(args.Contains("--sample-verify"))await VerificationTests.IntegrationAsync(AppPaths.Discover(),Assert);
if(args.Contains("--long"))await PipelineTests.LongAsync(AppPaths.Discover(),Assert);
if(args.Contains("--installer"))await InstallerTests.RunAsync(Assert);
int launcherArgument=Array.IndexOf(args,"--launcher-package");
if(launcherArgument>=0)await LauncherTests.RunAsync(args[launcherArgument+1],args[launcherArgument+2],Assert);
int packageArgument=Array.IndexOf(args,"--update-package");
if(packageArgument>=0)await UpdateTests.PackageAsync(args[packageArgument+1],Assert);
Console.WriteLine($"PASS {passed} assertions. Simulated devices are policy tests, not actual hardware qualification.");
