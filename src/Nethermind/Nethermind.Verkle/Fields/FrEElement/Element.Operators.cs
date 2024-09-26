using System.Numerics;
using Nethermind.Int256;
using FE = Nethermind.Verkle.Fields.FrEElement.FrE;

namespace Nethermind.Verkle.Fields.FrEElement;

public readonly partial struct FrE
{
    public static FE operator +(in FE a, in FE b)
    {
        AddMod(in a, in b, out FE res);
        return res;
    }

    public static FE operator -(in FE a, in FE b)
    {
        SubtractMod(in a, in b, out FE c);
        return c;
    }

    public static FE operator *(in FE a, in FE b)
    {
        MultiplyMod(a, b, out FE x);
        return x;
    }


    public static FE operator /(in FE a, in FE b)
    {
        Divide(in a, in b, out FE c);
        return c;
    }

    public static FE operator >> (in FE a, int n)
    {
        a.RightShift(n, out FE res);
        return res;
    }

    public static FE operator <<(in FE a, int n)
    {
        a.LeftShift(n, out FE res);
        return res;
    }

    public static bool operator ==(in FE a, int b)
    {
        return a.Equals(b);
    }

    public static bool operator ==(int a, in FE b)
    {
        return b.Equals(a);
    }

    public static bool operator ==(in FE a, uint b)
    {
        return a.Equals(b);
    }

    public static bool operator ==(uint a, in FE b)
    {
        return b.Equals(a);
    }

    public static bool operator ==(in FE a, long b)
    {
        return a.Equals(b);
    }

    public static bool operator ==(long a, in FE b)
    {
        return b.Equals(a);
    }

    public static bool operator ==(in FE a, ulong b)
    {
        return a.Equals(b);
    }

    public static bool operator ==(ulong a, in FE b)
    {
        return b.Equals(a);
    }

    public static bool operator !=(in FE a, int b)
    {
        return !a.Equals(b);
    }

    public static bool operator !=(int a, in FE b)
    {
        return !b.Equals(a);
    }

    public static bool operator !=(in FE a, uint b)
    {
        return !a.Equals(b);
    }

    public static bool operator !=(uint a, in FE b)
    {
        return !b.Equals(a);
    }

    public static bool operator !=(in FE a, long b)
    {
        return !a.Equals(b);
    }

    public static bool operator !=(long a, in FE b)
    {
        return !b.Equals(a);
    }

    public static bool operator !=(in FE a, ulong b)
    {
        return !a.Equals(b);
    }

    public static bool operator !=(ulong a, in FE b)
    {
        return !b.Equals(a);
    }

    public static bool operator <(in FE a, in FE b)
    {
        return LessThan(in a, in b);
    }

    public static bool operator <(in FE a, int b)
    {
        return LessThan(in a, b);
    }

    public static bool operator <(int a, in FE b)
    {
        return LessThan(a, in b);
    }

    public static bool operator <(in FE a, uint b)
    {
        return LessThan(in a, b);
    }

    public static bool operator <(uint a, in FE b)
    {
        return LessThan(a, in b);
    }

    public static bool operator <(in FE a, long b)
    {
        return LessThan(in a, b);
    }

    public static bool operator <(long a, in FE b)
    {
        return LessThan(a, in b);
    }

    public static bool operator <(in FE a, ulong b)
    {
        return LessThan(in a, b);
    }

    public static bool operator <(ulong a, in FE b)
    {
        return LessThan(a, in b);
    }

    public static bool operator <=(in FE a, in FE b)
    {
        return !LessThan(in b, in a);
    }

    public static bool operator <=(in FE a, int b)
    {
        return !LessThan(b, in a);
    }

    public static bool operator <=(int a, in FE b)
    {
        return !LessThan(in b, a);
    }

    public static bool operator <=(in FE a, uint b)
    {
        return !LessThan(b, in a);
    }

    public static bool operator <=(uint a, in FE b)
    {
        return !LessThan(in b, a);
    }

    public static bool operator <=(in FE a, long b)
    {
        return !LessThan(b, in a);
    }

    public static bool operator <=(long a, in FE b)
    {
        return !LessThan(in b, a);
    }

    public static bool operator <=(in FE a, ulong b)
    {
        return !LessThan(b, in a);
    }

    public static bool operator <=(ulong a, FE b)
    {
        return !LessThan(in b, a);
    }

    public static bool operator >(in FE a, in FE b)
    {
        return LessThan(in b, in a);
    }

    public static bool operator >(in FE a, int b)
    {
        return LessThan(b, in a);
    }

    public static bool operator >(int a, in FE b)
    {
        return LessThan(in b, a);
    }

    public static bool operator >(in FE a, uint b)
    {
        return LessThan(b, in a);
    }

    public static bool operator >(uint a, in FE b)
    {
        return LessThan(in b, a);
    }

    public static bool operator >(in FE a, long b)
    {
        return LessThan(b, in a);
    }

    public static bool operator >(long a, in FE b)
    {
        return LessThan(in b, a);
    }

    public static bool operator >(in FE a, ulong b)
    {
        return LessThan(b, in a);
    }

    public static bool operator >(ulong a, in FE b)
    {
        return LessThan(in b, a);
    }

    public static bool operator >=(in FE a, in FE b)
    {
        return !LessThan(in a, in b);
    }

    public static bool operator >=(in FE a, int b)
    {
        return !LessThan(in a, b);
    }

    public static bool operator >=(int a, in FE b)
    {
        return !LessThan(a, in b);
    }

    public static bool operator >=(in FE a, uint b)
    {
        return !LessThan(in a, b);
    }

    public static bool operator >=(uint a, in FE b)
    {
        return !LessThan(a, in b);
    }

    public static bool operator >=(in FE a, long b)
    {
        return !LessThan(in a, b);
    }

    public static bool operator >=(long a, in FE b)
    {
        return !LessThan(a, in b);
    }

    public static bool operator >=(in FE a, ulong b)
    {
        return !LessThan(in a, b);
    }

    public static bool operator >=(ulong a, in FE b)
    {
        return !LessThan(a, in b);
    }

    public static implicit operator FE(ulong value)
    {
        return new FE(value);
    }

    public static implicit operator FE(ulong[] value)
    {
        return new FE(value[0], value[1], value[2], value[3]);
    }

    public static explicit operator FE(in BigInteger value)
    {
        byte[] bytes32 = value.ToBytes32(true);
        return FromBytesReducedMultiple(bytes32, true);
    }
}
