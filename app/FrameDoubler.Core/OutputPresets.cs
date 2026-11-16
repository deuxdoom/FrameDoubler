using System.Text.Json.Serialization;

namespace FrameDoubler.Core;

// Four export presets: two source bitrate policies and two YouTube delivery sizes.
// Output settings are fixed so the selected policy is unambiguous.
public sealed record OutputPreset
{
    public string Id{get;init;}="";
    public string Name{get;init;}="";
    // youtube | none (plain MP4, audio copied)
    public string Platform{get;init;}="youtube";
    // null keeps the source resolution; otherwise the output height (width follows the aspect ratio).
    public int? Height{get;init;}
    // auto | source (same as the input when it can be encoded here) | h264 | hevc | av1
    public string Codec{get;init;}="auto";
    // platform (recommended bitrate of the target) | source (input bitrate scaled to the frame count) | bitrate | quality (CQ/CRF)
    public string RateControl{get;init;}="platform";
    public string Bitrate{get;init;}="";
    public int Quality{get;init;}=19;
    [JsonIgnore]public bool BuiltIn{get;init;}
}

public static class OutputPresets
{
    public static readonly IReadOnlyList<OutputPreset> BuiltIns=[
        // First and the default for new installs: keeps the source as it is and only adds frames.
        new(){Id="source",Platform="none",Codec="source",RateControl="source",BuiltIn=true},
        new(){Id="source-scaled",Platform="none",Codec="source",RateControl="source-scaled",BuiltIn=true},
        new(){Id="youtube-2160",Platform="youtube",Height=2160,Codec="h264",BuiltIn=true},
        new(){Id="youtube-1080",Platform="youtube",Height=1080,Codec="h264",BuiltIn=true},
    ];
    public static OutputPreset Find(string? key)=>FindExact(key)??BuiltIns[0];
    public static OutputPreset? FindExact(string? key)=>BuiltIns.FirstOrDefault(x=>x.Id==key);
    public static string DisplayName(OutputPreset preset,bool english)=>preset.Id switch{
        "source"=>english?"Source (keep bitrate)":"원본 (비트레이트 유지)",
        "source-scaled"=>english?"Source (bitrate scales with fps)":"원본 (fps 비례 비트레이트)",
        "youtube-2160"=>"YouTube 4K (2160p)","youtube-1080"=>"YouTube 1080p",_=>preset.Id};
    public static string Describe(OutputPreset preset,bool english)=>preset.Id switch{
        "source"=>english?"Keep the source resolution, codec when available and video bitrate, even at higher fps. Audio is passed through. VBR file size can vary."
            :"fps가 늘어도 원본 영상 비트레이트·해상도·가능한 원본 코덱을 유지합니다. 오디오는 패스스루합니다. VBR 특성상 파일 크기는 달라질 수 있습니다.",
        "source-scaled"=>english?"Source video bitrate × output fps / input file fps. Keep the source resolution and codec when available; pass audio through. A 60fps file of a 30fps game is already 60fps."
            :"원본 영상 비트레이트 × 출력 fps ÷ 입력 파일 fps를 사용합니다. 해상도·가능한 원본 코덱을 유지하고 오디오는 패스스루합니다. 30fps 게임이 60fps 파일로 녹화됐다면 입력은 60fps입니다.",
        _=>english?"MP4 / H.264 High / VBR / 4:2:0, 2 B frames, 0.5 s closed GOP. 4K 45 Mbps / 1080p 10 Mbps. HDR is tone-mapped to SDR. AAC-LC 48 kHz, 256 kbps (192 if the source is below 256 kbps or unknown); up to 60fps."
            :"MP4·H.264 High·VBR·4:2:0, B 프레임 2개, 0.5초 닫힌 GOP. 4K 45Mbps·1080p 10Mbps. HDR은 SDR로 톤 매핑합니다. AAC-LC 48kHz, 원본이 256kbps 이상이면 256kbps, 그보다 낮거나 정보가 없으면 192kbps, 60fps까지."};
    public static int AudioBitrate(AudioInfo source)=>source.Bitrate>=256_000?256_000:192_000;
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
            ??EncoderPolicy.DefaultCodec(encoders,options.Youtube,source.BitDepth>8,options.HardwareEncoding);
        var size=OutputSize(source.Width,source.Height,options.OutputHeight,source.SampleAspect);
        string? bitrate=options.Bitrate??(options.MatchSource?SourceBitrate(source,fps,options.ScaleSourceBitrate):options.QualityOnly?null:PlatformBitrate(options.Platform,size.Width,size.Height,fps));
        return (codec,bitrate);
    }
    // Recommended video bitrate for the target at the output size; null for "none".
    public static string? PlatformBitrate(string? platform,int width,int height,Rational fps){
        switch(platform){
            case "youtube":{
                int shortSide=Math.Min(width,height),longSide=Math.Max(width,height);
                if(shortSide<=1080&&longSide<=1920)return "10M";
                if(shortSide<=2160&&longSide<=3840)return "45M";
                throw new ArgumentException("YouTube preset supports up to 4K");
            }
            default:return null;
        }
    }
    // Keyframe interval in seconds for the target.
    public static double GopSeconds(string platform)=>.5;
    // Output frame size: the source size, or the preset size ("1080p") on the short side with an even
    // long side keeping the aspect ratio. A portrait 1080×1920 video therefore stays 1080p. An anamorphic
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
        Platform=preset.Platform=="none"?null:preset.Platform,
        OutputHeight=preset.Height,
        Codec=preset.Codec is "auto" or "source"?null:preset.Codec,
        MatchSource=preset.RateControl is "source" or "source-scaled",
        ScaleSourceBitrate=preset.RateControl=="source-scaled",
        Quality=Math.Clamp(preset.Quality,1,51),
        Bitrate=preset.RateControl=="bitrate"&&!string.IsNullOrWhiteSpace(preset.Bitrate)?preset.Bitrate.Trim():null,
        QualityOnly=preset.RateControl=="quality"};
}
