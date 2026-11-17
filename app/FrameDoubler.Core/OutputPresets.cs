using System.Text.Json.Serialization;

namespace FrameDoubler.Core;

// Two export presets, both keeping the source picture (size, pixel shape, codec when available) and
// copying the audio; they differ only in the video bitrate. Output settings are fixed so the policy is unambiguous.
public sealed record OutputPreset
{
    public string Id{get;init;}="";
    public string Name{get;init;}="";
    // auto | source (same as the input when it can be encoded here) | h264 | hevc | av1
    public string Codec{get;init;}="auto";
    // source (input bitrate) | source-scaled (input bitrate × output/input fps) | bitrate | quality (CQ/CRF)
    public string RateControl{get;init;}="source";
    public string Bitrate{get;init;}="";
    public int Quality{get;init;}=19;
    [JsonIgnore]public bool BuiltIn{get;init;}
}

public static class OutputPresets
{
    public static readonly IReadOnlyList<OutputPreset> BuiltIns=[
        // First and the default for new installs: keeps the source as it is and only adds frames.
        new(){Id="source",Codec="source",RateControl="source",BuiltIn=true},
        new(){Id="source-scaled",Codec="source",RateControl="source-scaled",BuiltIn=true},
    ];
    public static OutputPreset Find(string? key)=>FindExact(key)??BuiltIns[0];
    public static OutputPreset? FindExact(string? key)=>BuiltIns.FirstOrDefault(x=>x.Id==key);
    public static string DisplayName(OutputPreset preset,bool english)=>preset.Id switch{
        "source"=>english?"Source (keep bitrate)":"원본 (비트레이트 유지)",
        "source-scaled"=>english?"Source (bitrate scales with fps)":"원본 (fps 비례 비트레이트)",_=>preset.Id};
    public static string Describe(OutputPreset preset,bool english)=>preset.Id switch{
        "source"=>english?"Keep the source resolution, codec when available and video bitrate, even at higher fps. Audio is passed through. VBR file size can vary."
            :"fps가 늘어도 원본 영상 비트레이트·해상도·가능한 원본 코덱을 유지합니다. 오디오는 패스스루합니다. VBR 특성상 파일 크기는 달라질 수 있습니다.",
        "source-scaled"=>english?"Source video bitrate × output fps / input file fps. Keep the source resolution and codec when available; pass audio through. A 60fps file of a 30fps game is already 60fps."
            :"원본 영상 비트레이트 × 출력 fps ÷ 입력 파일 fps를 사용합니다. 해상도·가능한 원본 코덱을 유지하고 오디오는 패스스루합니다. 30fps 게임이 60fps 파일로 녹화됐다면 입력은 60fps입니다.",
        _=>""};
    // "24M", "8000k" or plain bits per second.
    public static long? ParseBitrate(string? value){
        if(string.IsNullOrWhiteSpace(value))return null;var v=value.Trim();
        long scale=char.ToLowerInvariant(v[^1]) switch{'m'=>1_000_000,'k'=>1_000,_=>1};
        return long.TryParse(scale==1?v:v[..^1],out long n)&&n>0?n*scale:null;
    }
    // Source codec when an available encoder can produce it here; null falls back to the default order.
    public static string? SourceCodec(MediaInfo source,IEnumerable<EncoderCapability> encoders,bool hardwareAllowed)=>
        source.Codec is "h264" or "hevc" or "av1"&&encoders.Any(x=>x.Available&&x.Codec==source.Codec&&(source.BitDepth<=8||x.TenBit)&&(hardwareAllowed||!x.Hardware))?source.Codec:null;
    // Exact source bitrate by default; optional output/input file fps multiplier with no 2x cap.
    public static string? SourceBitrate(MediaInfo source,Rational fps,bool scaleWithFps=false){
        if((source.VideoBitrate??source.OverallBitrate) is not long rate||rate<=0||source.Fps<=0)return null;
        double factor=scaleWithFps?fps.ToDouble()/source.Fps.ToDouble():1;
        return Math.Max(1,checked((long)Math.Round(rate*factor))).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    // Codec and bitrate for a conversion, shared by the pipeline and the time estimate.
    public static (string Codec,string? Bitrate) Encoding(MediaInfo source,ConversionOptions options,IEnumerable<EncoderCapability> encoders,Rational fps){
        string codec=options.Codec??(options.MatchSource?SourceCodec(source,encoders,options.HardwareEncoding):null)
            ??EncoderPolicy.DefaultCodec(encoders,source.BitDepth>8,options.HardwareEncoding);
        // No bitrate (no source bitrate, or the source policy is off) means quality-based (CQ/CRF) encoding.
        string? bitrate=options.Bitrate??(options.MatchSource?SourceBitrate(source,fps,options.ScaleSourceBitrate):null);
        return (codec,bitrate);
    }
    // Output frame size: the source size, or the requested size (CLI --height, "1080p") on the short side with
    // an even long side keeping the aspect ratio. A portrait 1080×1920 video therefore stays 1080p. An anamorphic
    // source is sized by its displayed shape and comes out with square pixels: 1920×1080 stored with
    // 81:256 pixels (shown 9:16) becomes 1080×1920.
    public static (int Width,int Height) OutputSize(int width,int height,int? target,Rational? sampleAspect=null){
        if(target is not int size||size<=0)return (width,height);
        bool square=sampleAspect is not { } sar||sar<=0||sar==1;
        double shown=square?width:width*sampleAspect!.Value.ToDouble();
        bool portrait=shown<height;
        double shortSide=portrait?shown:height,longSide=portrait?height:shown;
        if(square&&size==(portrait?width:height))return (width,height);
        int other=Math.Max(2,(int)Math.Round(longSide*size/shortSide/2)*2),side=size-(size%2);
        return portrait?(side,other):(other,side);
    }
    public static ConversionOptions Apply(OutputPreset preset,ConversionOptions options)=>options with{
        OutputHeight=null,
        Codec=preset.Codec is "auto" or "source"?null:preset.Codec,
        MatchSource=preset.RateControl is "source" or "source-scaled",
        ScaleSourceBitrate=preset.RateControl=="source-scaled",
        Quality=Math.Clamp(preset.Quality,1,51),
        Bitrate=preset.RateControl=="bitrate"&&!string.IsNullOrWhiteSpace(preset.Bitrate)?preset.Bitrate.Trim():null};
}
