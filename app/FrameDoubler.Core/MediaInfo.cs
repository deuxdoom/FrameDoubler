using System.Text.Json;
using System.Text.RegularExpressions;

namespace FrameDoubler.Core;

public sealed record AudioInfo(int Index,string Codec,int Channels,int SampleRate,Rational Start,Rational? Duration,Rational TimeBase)
{public long? Bitrate{get;init;}}
// The original signal of an HDR input. MediaInfo then describes the SDR frames it is processed as.
public sealed record HdrSource(string Transfer,string? Primaries,string? Matrix,string? Range,int BitDepth)
{
    public string Name=>Transfer=="arib-std-b67"?"HLG":"PQ";
    public static bool Is(string? transfer)=>transfer is "smpte2084" or "arib-std-b67";
}
public sealed record MediaInfo(string Path,int Width,int Height,string Codec,string PixelFormat,int BitDepth,
    Rational TimeBase,Rational Fps,Rational NominalFps,bool IsVfr,Rational Duration,long[] Pts,
    string? ColorSpace,string? ColorRange,string? ColorPrimaries,string? ColorTransfer,AudioInfo[] Audio)
{
    public HdrSource? HdrInput{get;init;}
    public bool Hdr=>HdrInput is not null;
    // Clockwise display rotation from the container (0, 90, 180 or 270). FFmpeg turns decoded frames
    // upright by default, so Width and Height already describe the upright (displayed) picture.
    public int Rotation{get;init;}
    // RIFE motion estimation runs at half resolution only for footage larger than 1440p. The short
    // side decides, so a portrait phone video is treated like the same landscape resolution.
    public double MotionScale=>Math.Min(Width,Height)>1440?.5:1;
    public int FrameBytes=>checked(Width*Height*3*(BitDepth>8?2:1));
    public Rational Start=>Pts.Length>0?(Rational)Pts[0]*TimeBase:ReportedStart;
    public Rational ReportedStart{get;init;}
    public long? ReportedFrames{get;init;}
    public long FrameCount=>Pts.Length>0?Pts.LongLength:ReportedFrames??(Duration*Fps).Ceiling();
    public long? VideoBitrate{get;init;}
    public long? OverallBitrate{get;init;}
    public bool OverallBitrateEstimated{get;init;}
}

public static class MediaProbe
{
    public static async Task<MediaInfo> ReadAsync(AppPaths paths,string path,CancellationToken cancel=default,bool metadataOnly=false)
    {
        path=System.IO.Path.GetFullPath(path);
        if(!File.Exists(path))throw new FileNotFoundException("Input video not found",path);
        var result=await ProcessRunner.RunAsync(paths.FFprobe,["-v","error","-show_streams","-show_format","-of","json",path],cancel);
        if(result.ExitCode!=0)throw new IOException("Cannot inspect video: "+result.Error);
        using var document=JsonDocument.Parse(result.Output);
        var streams=document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.FirstOrDefault(x=>Text(x,"codec_type")=="video" &&
            !(x.TryGetProperty("disposition",out var disp)&&Number(disp,"attached_pic")==1));
        if(video.ValueKind==JsonValueKind.Undefined)throw new IOException("No video stream");
        var format=document.RootElement.GetProperty("format");
        Rational metadataRate=Rate(video,"avg_frame_rate");if(metadataRate<=0)metadataRate=Rate(video,"r_frame_rate");
        if(metadataOnly){
            Rational length=Number(video,"duration_ts") is >0 and var ticks?ticks*Rate(video,"time_base"):OptionalRate(video,"duration")??Rate(format,"duration");
            if(length<=0||metadataRate<=0)throw new IOException("Cannot determine video duration/frame rate");
            Rational nominalRate=Rate(video,"r_frame_rate");
            return Create(path,video,streams,Rate(video,"time_base"),metadataRate,nominalRate,nominalRate>0&&metadataRate!=nominalRate,length,[],format);
        }
        var packets=await ProcessRunner.RunAsync(paths.FFprobe,["-v","error","-select_streams",Number(video,"index").ToString(),
            "-show_entries","packet=pts,duration,flags","-of","json",path],cancel,TimeSpan.FromMinutes(5));
        if(packets.ExitCode!=0)throw new IOException(packets.Error);
        using var packetDoc=JsonDocument.Parse(packets.Output);
        // Packets flagged D (discard) lie outside the MP4/MOV edit list, e.g. the key frame before the
        // cut point of a losslessly trimmed clip: they are decoded for reference but never output.
        var timed=packetDoc.RootElement.GetProperty("packets").EnumerateArray().Where(x=>x.TryGetProperty("pts",out _)&&!(Text(x,"flags")??"").Contains('D'))
            .Select(x=>(Pts:Long(x,"pts"),Duration:Long(x,"duration"))).OrderBy(x=>x.Pts).ToArray();
        if(timed.Length==0)throw new IOException("No timestamped video frames");
        Rational timeBase=Rate(video,"time_base"),nominal=Rate(video,"r_frame_rate");
        if(nominal<=0)nominal=Rate(video,"avg_frame_rate");
        if(timeBase<=0)throw new IOException("Invalid video time base");
        long[] pts=timed.Select(x=>x.Pts).ToArray();
        long[] deltas=pts.Zip(pts.Skip(1),(a,b)=>b-a).ToArray();
        if(deltas.Any(x=>x<=0))throw new IOException("Non-increasing video presentation timestamps");
        long last=timed[^1].Duration;
        if(last<=0)last=deltas.Length>0?deltas.Min():nominal>0?((Rational)1/(nominal*timeBase)).Ceiling():1;
        Rational duration=(Rational)(pts[^1]-pts[0]+last)*timeBase;
        bool vfr=deltas.Length>0&&deltas.Max()-deltas.Min()>1;
        Rational fps=vfr||nominal<=0?(Rational)pts.Length/duration:nominal;
        return Create(path,video,streams,timeBase,fps,nominal,vfr,duration,pts,format);
    }
    private static MediaInfo Create(string path,JsonElement video,JsonElement[] streams,Rational timeBase,Rational fps,Rational nominal,bool vfr,Rational duration,long[] pts,JsonElement format){
        string pixel=Text(video,"pix_fmt")??"";
        int depth=(int)Number(video,"bits_per_raw_sample");
        if(depth==0) { var match=Regex.Match(pixel,@"(?:p|gbrp|gray)(9|10|12|14|16)"); depth=match.Success?int.Parse(match.Groups[1].Value):pixel.StartsWith("p010",StringComparison.Ordinal)?10:8; }
        var audio=streams.Where(x=>Text(x,"codec_type")=="audio").Select(x=>new AudioInfo((int)Number(x,"index"),Text(x,"codec_name")??"",
            (int)Number(x,"channels"),(int)Number(x,"sample_rate"),Rate(x,"start_time"),OptionalRate(x,"duration"),Rate(x,"time_base")){Bitrate=PositiveBitrate(x)}).ToArray();
        string? space=Text(video,"color_space"),range=Text(video,"color_range"),primaries=Text(video,"color_primaries"),transfer=Text(video,"color_transfer");
        HdrSource? hdr=HdrSource.Is(transfer)?new(transfer!,primaries,space,range,depth):null;
        // HDR is converted to SDR when decoding (see ToneMap), so downstream stages work on 8-bit BT.709.
        if(hdr is not null){space=primaries=transfer="bt709";range="tv";depth=8;}
        // A quarter-turn display matrix (portrait phone recordings) swaps the decoded frame size.
        int rotation=DisplayRotation(video),width=(int)Number(video,"width"),height=(int)Number(video,"height");
        if(rotation is 90 or 270)(width,height)=(height,width);
        return new(path,width,height,Text(video,"codec_name")??"",pixel,depth,timeBase,fps,nominal,vfr,duration,pts,
            space,range,primaries,transfer,audio){HdrInput=hdr,Rotation=rotation,
                VideoBitrate=PositiveBitrate(video),OverallBitrate=PositiveBitrate(format)??(long?)Math.Round(new FileInfo(path).Length*8/duration.ToDouble()),
                OverallBitrateEstimated=PositiveBitrate(format) is null,ReportedStart=video.TryGetProperty("start_pts",out _)?Number(video,"start_pts")*timeBase:Rate(video,"start_time"),ReportedFrames=Number(video,"nb_frames") is >0 and var count?count:null};
    }
    // FFmpeg's decoder applies the display matrix only for right angles (autorotate); other angles stay
    // unrotated, so they report 0 here. ffprobe prints the matrix angle counter-clockwise; older files
    // may only carry the clockwise "rotate" tag.
    public static int DisplayRotation(JsonElement video){
        double? clockwise=null;
        if(video.TryGetProperty("side_data_list",out var list)&&list.ValueKind==JsonValueKind.Array)
            foreach(var item in list.EnumerateArray())
                if(item.ValueKind==JsonValueKind.Object&&item.TryGetProperty("rotation",out var value)&&Degrees(value) is double angle){clockwise=-angle;break;}
        if(clockwise is null&&video.TryGetProperty("tags",out var tags)&&tags.ValueKind==JsonValueKind.Object&&tags.TryGetProperty("rotate",out var tag))clockwise=Degrees(tag);
        if(clockwise is not double degrees||!double.IsFinite(degrees))return 0;
        double quarter=Math.Round(degrees/90);
        if(Math.Abs(degrees-quarter*90)>1)return 0;
        return (int)(((long)quarter%4+4)%4)*90;
        static double? Degrees(JsonElement value)=>value.ValueKind switch{
            JsonValueKind.Number=>value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double parsed)=>parsed,
            _=>null};
    }
    public static long? PositiveBitrate(JsonElement element)=>Number(element,"bit_rate") is >0 and var value?value:null;
    private static string? Text(JsonElement e,string name)=>e.TryGetProperty(name,out var value)?value.ToString():null;
    private static long Number(JsonElement e,string name)=>long.TryParse(Text(e,name),out var n)?n:0;
    private static long Long(JsonElement e,string name)=>Number(e,name);
    private static Rational Rate(JsonElement e,string name)=>OptionalRate(e,name)??0;
    private static Rational? OptionalRate(JsonElement e,string name) {
        string? s=Text(e,name); if(s is null or "N/A" or "0/0")return null;
        return Rational.Parse(s);
    }
}
