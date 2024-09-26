using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Int256;
using FE = Nethermind.Verkle.Fields.FpEElement.FpE;

namespace Nethermind.Verkle.Fields.FpEElement;

public readonly partial struct FpE
{
    public new string ToString()
    {
        return $"{nameof(FE)} [{u0} {u1} {u2} {u3}]";
    }

    public bool Bit(int n)
    {
        int bucket = n / 64 % 4;
        int position = n % 64;
        return (this[bucket] & ((ulong)1 << position)) != 0;
    }

    public int BitLen()
    {
        return u3 != 0
            ? 192 + Len64(u3)
            : u2 != 0
                ? 128 + Len64(u2)
                : u1 != 0
                    ? 64 + Len64(u1)
                    : Len64(u0);
    }

    public static FE SetElement(ulong u0 = 0, ulong u1 = 0, ulong u2 = 0, ulong u3 = 0)
    {
        FE newElem = new(u0, u1, u2, u3);
        ToMontgomery(in newElem, out FE res);
        return res;
    }

    public static FE SetElement(BigInteger value)
    {
        FE newElem = new(value);
        ToMontgomery(in newElem, out FE res);
        return res;
    }

    public byte[] ToBytes()
    {
        byte[] returnEncoding = new byte[32];
        Span<byte> target = returnEncoding;
        ToRegular(in this, out FE x);
        ToLittleEndian(x.u0, x.u1, x.u2, x.u3, in target);
        return returnEncoding;
    }

    public void ToBytes(in Span<byte> target)
    {
        ToRegular(in this, out FE x);
        ToLittleEndian(x.u0, x.u1, x.u2, x.u3, in target);
    }

    public byte[] ToBytesBigEndian()
    {
        byte[] returnEncoding = new byte[32];
        Span<byte> target = returnEncoding;
        ToRegular(in this, out FE x);
        ToBigEndian(x.u0, x.u1, x.u2, x.u3, in target);
        return returnEncoding;
    }

    public void ToBytesBigEndian(in Span<byte> target)
    {
        ToRegular(in this, out FE x);
        ToBigEndian(x.u0, x.u1, x.u2, x.u3, in target);
    }

    public static FE FromBytes(in ReadOnlySpan<byte> byteEncoded, bool isBigEndian = false)
    {
        FE elem = new(byteEncoded, isBigEndian);
        ToMontgomery(in elem, out elem);
        return elem;
    }

    public static FE FromBytesReduced(in ReadOnlySpan<byte> byteEncoded, bool isBigEndian = false)
    {
        FE inp = new(byteEncoded, isBigEndian);
        if (LessThan(in qElement, in inp))
            if (SubtractUnderflow(inp, qElement, out inp))
                ThrowInvalidConstraintException();
        ToMontgomery(inp, out FE resF);
        return resF;
    }

    private static FE FromBytesReducedMultiple(in ReadOnlySpan<byte> bytes, bool isBigEndian = false)
    {
        UInt256 val = new(bytes, isBigEndian);
        val.Mod(_modulus.Value, out UInt256 res);
        FE inp = new(res.u0, res.u1, res.u2, res.u3);
        ToMontgomery(inp, out FE elem);
        return elem;
    }

    public static void ToMontgomery(in FE x, out FE z)
    {
        MultiplyMod(x, rSquare, out z);
    }

    public static void ToRegular(in FE x, out FE z)
    {
        FromMontgomery(in x, out z);
    }

    public static void FromMontgomery(in FE x, out FE res)
    {
        U4 z = new() { u0 = x.u0, u1 = x.u1, u2 = x.u2, u3 = x.u3 };

        ulong m = z.u0 * QInvNeg;
        ulong c = MAdd0(m, Q0, z.u0);
        c = MAdd2(m, Q1, z.u1, c, out z.u0);
        c = MAdd2(m, Q2, z.u2, c, out z.u1);
        c = MAdd2(m, Q3, z.u3, c, out z.u2);
        z.u3 = c;

        m = z.u0 * QInvNeg;
        c = MAdd0(m, Q0, z.u0);
        c = MAdd2(m, Q1, z.u1, c, out z.u0);
        c = MAdd2(m, Q2, z.u2, c, out z.u1);
        c = MAdd2(m, Q3, z.u3, c, out z.u2);
        z.u3 = c;

        m = z.u0 * QInvNeg;
        c = MAdd0(m, Q0, z.u0);
        c = MAdd2(m, Q1, z.u1, c, out z.u0);
        c = MAdd2(m, Q2, z.u2, c, out z.u1);
        c = MAdd2(m, Q3, z.u3, c, out z.u2);
        z.u3 = c;

        m = z.u0 * QInvNeg;
        c = MAdd0(m, Q0, z.u0);
        c = MAdd2(m, Q1, z.u1, c, out z.u0);
        c = MAdd2(m, Q2, z.u2, c, out z.u1);
        c = MAdd2(m, Q3, z.u3, c, out z.u2);
        z.u3 = c;
        Unsafe.SkipInit(out res);
        Unsafe.As<FE, U4>(ref res) = z;
        if (LessThan(qElement, res)) SubtractUnderflow(res, qElement, out res);
    }
}
