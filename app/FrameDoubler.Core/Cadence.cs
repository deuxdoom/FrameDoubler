using System.Buffers;

namespace FrameDoubler.Core;

public sealed class VideoFrame : IDisposable
{
    public byte[] Buffer { get; }
    public int Length { get; }
    public int Index { get; }
    public Rational Time { get; }
    private bool disposed;
    public VideoFrame(int length,int index,Rational time) { Buffer=ArrayPool<byte>.Shared.Rent(length); Length=length; Index=index; Time=time; }
    public Memory<byte> Memory=>Buffer.AsMemory(0,Length);
    public void Dispose() { if(!disposed) { disposed=true;ArrayPool<byte>.Shared.Return(Buffer); } }
}
public enum FrameLabel { Unique, Duplicate, Static }
public sealed record Signature(int Width,int Height,float[] Luma)
{
    public static Signature Create(VideoFrame frame,MediaInfo info) {
        int step=Math.Max(1,info.Width/480),w=(info.Width+step-1)/step,h=(info.Height+step-1)/step;
        var luma=new float[w*h];
        bool deep=info.BitDepth>8;
        for(int y=0;y<h;y++)for(int x=0;x<w;x++) {
            int p=(y*step*info.Width+x*step)*3*(deep?2:1);
            float r=deep?BitConverter.ToUInt16(frame.Buffer,p)/257f:frame.Buffer[p];
            float g=deep?BitConverter.ToUInt16(frame.Buffer,p+2)/257f:frame.Buffer[p+1];
            float b=deep?BitConverter.ToUInt16(frame.Buffer,p+4)/257f:frame.Buffer[p+2];
            luma[y*w+x]=r*.2126f+g*.7152f+b*.0722f;
        }
        return new(w,h,luma);
    }
    public float Difference(Signature other) {
        float maximum=0;
        for(int by=0;by<Math.Max(1,Height/16);by++)for(int bx=0;bx<Math.Max(1,Width/16);bx++) {
            float total=0;int count=0;
            for(int y=by*16;y<Math.Min((by+1)*16,Height);y++)for(int x=bx*16;x<Math.Min((bx+1)*16,Width);x++) {
                int i=y*Width+x;total+=Math.Abs(Luma[i]-other.Luma[i]);count++;
            }
            maximum=Math.Max(maximum,total/Math.Max(1,count));
        }
        return maximum;
    }
    public float[] Descriptor() {
        float[] hist=new float[16*64];
        for(int i=0;i<4;i++)for(int j=0;j<4;j++) {
            int count=0,start=(i*4+j)*64;
            for(int y=i*Height/4;y<(i+1)*Height/4;y++)for(int x=j*Width/4;x<(j+1)*Width/4;x++) {
                int bin=Math.Clamp((int)Luma[y*Width+x]/4,0,63);hist[start+bin]++;count++;
            }
            for(int k=0;k<64;k++)hist[start+k]/=Math.Max(1,count);
        }
        return hist;
    }
    public static float CutScore(float[] a,float[] b) { float d=0;for(int i=0;i<a.Length;i++)d+=Math.Abs(a[i]-b[i]);return d/32; }
}

public sealed class CadenceDetector
{
    private Signature? previous;
    private float? pending,before;
    private int count,run;
    private readonly Queue<float> noise=new();
    private readonly float maxDifference,initialFloor,duplicateRatio;
    public CadenceDetector(float maxDifference=4,float initialFloor=.5f,float duplicateRatio=.5f) {this.maxDifference=maxDifference;this.initialFloor=initialFloor;this.duplicateRatio=duplicateRatio;}
    public FrameLabel? Push(Signature signature) {
        count++;
        if(previous is null){previous=signature;return null;}
        float current=previous.Difference(signature);previous=signature;
        if(count==2){pending=current;return FrameLabel.Unique;}
        var label=Decide(pending!.Value,before,current);before=pending;pending=current;return label;
    }
    public FrameLabel? Flush()=>count==0?null:count==1?FrameLabel.Unique:Decide(pending!.Value,before,null);
    private FrameLabel Decide(float own,float? left,float? right) {
        float contrast=Math.Max(left??0,right??0);
        bool duplicate=(left.HasValue||right.HasValue)&&own<maxDifference&&own<=duplicateRatio*contrast;
        if(duplicate){noise.Enqueue(own);if(noise.Count>31)noise.Dequeue();}
        float floor=initialFloor;
        if(noise.Count>=5){var sorted=noise.Order().ToArray();float median=(sorted[(sorted.Length-1)/2]+sorted[sorted.Length/2])/2;floor=Math.Min(2.5f*median,maxDifference);}
        if(!(duplicate||own<floor)){run=0;return FrameLabel.Unique;}
        return ++run<=4?FrameLabel.Duplicate:FrameLabel.Static;
    }
}

public readonly record struct HudRegion(int X,int Y,int Width,int Height)
{
    public void Validate(MediaInfo info){if(X<0||Y<0||Width<=0||Height<=0||X+Width>info.Width||Y+Height>info.Height)throw new ArgumentException("HUD region outside image");}
    public void Restore(VideoFrame first,VideoFrame second,byte[] middle,MediaInfo info) {
        int pixel=info.BitDepth>8?6:3;
        for(int y=Y;y<Y+Height;y++)for(int x=X;x<X+Width;x++) {
            int offset=(y*info.Width+x)*pixel;
            if(first.Buffer.AsSpan(offset,pixel).SequenceEqual(second.Buffer.AsSpan(offset,pixel)))
                first.Buffer.AsSpan(offset,pixel).CopyTo(middle.AsSpan(offset,pixel));
        }
    }
}
