using System.Numerics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameDoubler.Core;

[JsonConverter(typeof(RationalJsonConverter))]
public readonly record struct Rational : IComparable<Rational>
{
    public BigInteger Numerator { get; }
    private readonly BigInteger denominator;
    public BigInteger Denominator => denominator.IsZero ? BigInteger.One : denominator;
    public Rational(BigInteger numerator, BigInteger denominator) {
        if (denominator.IsZero) throw new DivideByZeroException();
        if (denominator.Sign < 0) { numerator=-numerator; denominator=-denominator; }
        var gcd=BigInteger.GreatestCommonDivisor(numerator,denominator);
        Numerator=numerator/gcd; this.denominator=denominator/gcd;
    }
    public static Rational Parse(string value) {
        string[] pair=value.Split('/');
        if(pair.Length==2) return new(BigInteger.Parse(pair[0],CultureInfo.InvariantCulture),BigInteger.Parse(pair[1],CultureInfo.InvariantCulture));
        if(pair.Length!=1) throw new FormatException("Invalid rational");
        decimal number=decimal.Parse(value,CultureInfo.InvariantCulture);
        int[] bits=decimal.GetBits(number);
        BigInteger n=(uint)bits[0]+((BigInteger)(uint)bits[1]<<32)+((BigInteger)(uint)bits[2]<<64);
        if(bits[3]<0)n=-n;
        return new(n,BigInteger.Pow(10,(bits[3]>>16)&255));
    }
    public static implicit operator Rational(int value)=>new(value,1);
    public static implicit operator Rational(long value)=>new(value,1);
    public static Rational operator +(Rational a,Rational b)=>new(a.Numerator*b.Denominator+b.Numerator*a.Denominator,a.Denominator*b.Denominator);
    public static Rational operator -(Rational a,Rational b)=>new(a.Numerator*b.Denominator-b.Numerator*a.Denominator,a.Denominator*b.Denominator);
    public static Rational operator *(Rational a,Rational b)=>new(a.Numerator*b.Numerator,a.Denominator*b.Denominator);
    public static Rational operator /(Rational a,Rational b)=>new(a.Numerator*b.Denominator,a.Denominator*b.Numerator);
    public static bool operator <(Rational a,Rational b)=>a.CompareTo(b)<0;
    public static bool operator >(Rational a,Rational b)=>a.CompareTo(b)>0;
    public static bool operator <=(Rational a,Rational b)=>a.CompareTo(b)<=0;
    public static bool operator >=(Rational a,Rational b)=>a.CompareTo(b)>=0;
    public int CompareTo(Rational other)=>(Numerator*other.Denominator).CompareTo(other.Numerator*Denominator);
    public long Ceiling() { var q=BigInteger.DivRem(Numerator,Denominator,out var r); return checked((long)(r.Sign>0?q+1:q)); }
    public double ToDouble()=>(double)Numerator/(double)Denominator;
    public bool Equals(Rational other)=>Numerator==other.Numerator && Denominator==other.Denominator;
    public override int GetHashCode()=>HashCode.Combine(Numerator,Denominator);
    public string Seconds(){var n=(BigInteger.Abs(Numerator)*1_000_000_000+Denominator/2)/Denominator;
        return $"{(Numerator.Sign<0?"-":"")}{n/1_000_000_000}.{(n%1_000_000_000).ToString("D9",CultureInfo.InvariantCulture)}";}
    public override string ToString()=>Denominator.IsOne?Numerator.ToString(CultureInfo.InvariantCulture):$"{Numerator}/{Denominator}";
}
public sealed class RationalJsonConverter:JsonConverter<Rational> {
    public override Rational Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>Rational.Parse(reader.GetString()??"0");
    public override void Write(Utf8JsonWriter writer,Rational value,JsonSerializerOptions options)=>writer.WriteStringValue(value.ToString());
}

public static class Timeline
{
    public static Rational DefaultRate(Rational input)=>input<=0||input>121?60:input>45?input:input*2;
    public static IEnumerable<(long Index,float Position)> Steps(Rational first,Rational second,Rational fps) {
        if(second<=first)yield break;
        for(long k=(first*fps).Ceiling();k<(second*fps).Ceiling();k++) {
            var p=((Rational)k/fps-first)/(second-first);
            yield return (k,p<=new Rational(1,20)?0:p>=new Rational(19,20)?1:(float)p.ToDouble());
        }
    }
}
