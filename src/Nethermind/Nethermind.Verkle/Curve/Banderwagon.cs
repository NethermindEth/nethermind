// Copyright 2022 Demerzel Solutions Limited
// Licensed under Apache-2.0.For full terms, see LICENSE in the project root.

using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Int256;
using Nethermind.Verkle.Fields.FpEElement;
using Nethermind.Verkle.Fields.FrEElement;

[assembly: InternalsVisibleTo("Nethermind.Verkle.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Verkle.Bench")]

namespace Nethermind.Verkle.Curve;

public readonly partial struct Banderwagon
{
    public readonly FpE X;
    public readonly FpE Y;
    public readonly FpE Z;

    public Banderwagon()
    {
        X = FpE.Zero;
        Y = FpE.One;
        Z = FpE.One;
    }

    public Banderwagon(FpE x, FpE y)
    {
        X = x;
        Y = y;
        Z = FpE.One;
    }

    private Banderwagon(FpE x, FpE y, FpE z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    private Banderwagon(AffinePoint p)
    {
        X = p.X;
        Y = p.Y;
        Z = FpE.One;
    }

    internal Banderwagon(string serializedHexBigEndian)
    {
        this = FromBytes(Convert.FromHexString(serializedHexBigEndian), true, false)!.Value;
    }

    private static FpE A => CurveParams.A;
    private static FpE D => CurveParams.D;

    public static Banderwagon Identity = new(AffinePoint.Identity);

    public static Banderwagon Generator = new(AffinePoint.Generator);

    public static Banderwagon? FromBytes(byte[] bytes, bool isBigEndian = true, bool subgroupCheck = true)
    {
        FpE x = FpE.FromBytes(bytes, isBigEndian);

        FpE? y = AffinePoint.GetYCoordinate(x, true);
        if (y is null) return null;

        if (!subgroupCheck) return new Banderwagon(x, y.Value);
        return SubgroupCheck(x) != 1 ? null : new Banderwagon(x, y.Value);
    }

    public static Banderwagon FromBytesUncompressedUnchecked(ReadOnlySpan<byte> bytes, bool isBigEndian = true)
    {
        FpE x = FpE.FromBytes(bytes[..32], isBigEndian);
        FpE y = FpE.FromBytes(bytes[32..], isBigEndian);
        return new Banderwagon(x, y);
    }

    public byte[] ToBytesUncompressed()
    {
        byte[] uncompressed = new byte[64];
        Span<byte> ucSpan = uncompressed;
        AffinePoint affine = ToAffine();

        Span<byte> x = ucSpan[..32];
        affine.X.ToBytesBigEndian(in x);
        Span<byte> y = ucSpan[32..];
        affine.Y.ToBytesBigEndian(in y);

        return uncompressed;
    }

    public void ToBytesUncompressed(in Span<byte> target)
    {
        AffinePoint affine = ToAffine();

        Span<byte> x = target[..32];
        affine.X.ToBytesBigEndian(in x);
        Span<byte> y = target[32..];
        affine.Y.ToBytesBigEndian(in y);
    }

    public byte[] ToBytesUncompressedLittleEndian()
    {
        byte[] uncompressed = new byte[64];
        Span<byte> ucSpan = uncompressed;
        AffinePoint affine = ToAffine();

        Span<byte> x = ucSpan[..32];
        affine.X.ToBytes(in x);
        Span<byte> y = ucSpan[32..];
        affine.Y.ToBytes(in y);

        return uncompressed;
    }

    public void ToBytesUncompressedLittleEndian(in Span<byte> target)
    {
        AffinePoint affine = ToAffine();

        Span<byte> x = target[..32];
        affine.X.ToBytes(in x);
        Span<byte> y = target[32..];
        affine.Y.ToBytes(in y);
    }

    private static int SubgroupCheck(FpE x)
    {
        FpE.MultiplyMod(x, x, out FpE res);
        FpE.MultiplyMod(res, A, out res);
        res = res.Negative();
        FpE.AddMod(res, FpE.One, out res);

        return FpE.Legendre(in res);
    }

    public static Banderwagon Neg(Banderwagon p)
    {
        return new Banderwagon(p.X.Negative(), p.Y, p.Z);
    }

    // https://hyperelliptic.org/EFD/g1p/auto-twisted-projective.html
    public static Banderwagon Add(Banderwagon p, Banderwagon q)
    {
        FpE x1 = p.X;
        FpE y1 = p.Y;
        FpE z1 = p.Z;

        FpE x2 = q.X;
        FpE y2 = q.Y;
        FpE z2 = q.Z;

        FpE a = z1 * z2;
        FpE b = a * a;

        FpE c = x1 * x2;

        FpE d = y1 * y2;

        FpE e = D * c * d;

        FpE f = b - e;
        FpE g = b + e;

        FpE x3 = a * f * (((x1 + y1) * (x2 + y2)) - c - d);
        FpE y3 = a * g * (d - (A * c));
        FpE z3 = f * g;

        return new Banderwagon(x3, y3, z3);
    }

    public static Banderwagon Add(Banderwagon p, AffinePoint q)
    {
        FpE x1 = p.X;
        FpE y1 = p.Y;
        FpE z1 = p.Z;

        FpE x2 = q.X;
        FpE y2 = q.Y;

        FpE b = z1 * z1;

        FpE c = x1 * x2;

        FpE d = y1 * y2;

        FpE e = D * c * d;

        FpE f = b - e;
        FpE g = b + e;

        FpE x3 = z1 * f * (((x1 + y1) * (x2 + y2)) - c - d);
        FpE y3 = z1 * g * (d - (A * c));
        FpE z3 = f * g;

        return new Banderwagon(x3, y3, z3);
    }

    public static Banderwagon Sub(Banderwagon p, Banderwagon q)
    {
        return Add(p, Neg(q));
    }

    public FrE MapToScalarField()
    {
        FpE.Inverse(in Y, out FpE map);
        FpE.MultiplyMod(in X, in map, out map);
        FpE.FromMontgomery(in map, out map);
        Unsafe.As<FpE, UInt256>(ref Unsafe.AsRef(in map)).Mod(FrE._modulus.Value, out UInt256 inter);
        return FrE.SetElement(inter.u0, inter.u1, inter.u2, inter.u3);
    }

    public FrE MapToScalarField(in FpE inv)
    {
        FpE.MultiplyMod(in X, in inv, out FpE map);
        FpE.FromMontgomery(in map, out map);
        Unsafe.As<FpE, UInt256>(ref Unsafe.AsRef(in map)).Mod(FrE._modulus.Value, out UInt256 inter);
        return FrE.SetElement(inter.u0, inter.u1, inter.u2, inter.u3);
    }

    public static FrE[] BatchMapToScalarField(Banderwagon[] points)
    {
        FpE[] inverses = points.Select(x => x.Y).ToArray();
        inverses = FpE.MultiInverse(inverses);

        FrE[] fields = new FrE[points.Length];
        Parallel.For(0, points.Length, i =>
        {
            fields[i] = points[i].MapToScalarField(in inverses[i]);
        });
        return fields;
    }

    public bool IsZero => X.IsZero && Y.Equals(Z) && !Y.IsZero;

    public AffinePoint ToAffine()
    {
        if (IsZero) return AffinePoint.Identity;
        if (Z.IsZero) ThrowInvalidConstraintException();
        if (Z.IsOne) return new AffinePoint(X, Y);

        FpE.Inverse(Z, out FpE zInv);
        FpE xAff = X * zInv;
        FpE yAff = Y * zInv;

        return new AffinePoint(xAff, yAff);
    }

    public AffinePoint ToAffine(in FpE zInv)
    {
        if (IsZero) return AffinePoint.Identity;
        if (Z.IsZero) ThrowInvalidConstraintException();
        if (Z.IsOne) return new AffinePoint(X, Y);

        FpE xAff = X * zInv;
        FpE yAff = Y * zInv;

        return new AffinePoint(xAff, yAff);
    }

    public static byte[] ToBytes(in AffinePoint normalizedPoint)
    {
        FpE x = normalizedPoint.X;
        if (normalizedPoint.Y.LexicographicallyLargest() == false) x = x.Negative();

        return x.ToBytesBigEndian();
    }

    public byte[] ToBytes()
    {
        AffinePoint affine = ToAffine();
        FpE x = affine.X;
        if (affine.Y.LexicographicallyLargest() == false) x = affine.X.Negative();

        return x.ToBytesBigEndian();
    }

    public byte[] ToBytesLittleEndian()
    {
        AffinePoint affine = ToAffine();
        FpE x = affine.X;
        if (affine.Y.LexicographicallyLargest() == false) x = affine.X.Negative();

        return x.ToBytes();
    }

    public static Banderwagon Double(Banderwagon p)
    {
        FpE x1 = p.X;
        FpE y1 = p.Y;
        FpE z1 = p.Z;

        FpE b = (x1 + y1) * (x1 + y1);
        FpE c = x1 * x1;
        FpE d = y1 * y1;

        FpE e = A * c;
        FpE f = e + d;
        FpE h = z1 * z1;
        FpE j = f - (h + h);

        FpE x3 = (b - c - d) * j;
        FpE y3 = f * (e - d);
        FpE z3 = f * j;
        return new Banderwagon(x3, y3, z3);
    }

    public bool IsOnCurve()
    {
        return ToAffine().IsOnCurve();
    }

    public static Banderwagon ScalarMul(in Banderwagon point, in FrE scalarMont)
    {
        Banderwagon result = Identity;

        FrE.ToRegular(in scalarMont, out FrE scalar);

        int len = scalar.BitLen();
        for (int i = len; i >= 0; i--)
        {
            result = Double(result);
            if (scalar.Bit(i)) result += point;
        }

        return result;
    }


    public static Banderwagon TwoTorsionPoint()
    {
        AffinePoint affinePoint = new(FpE.Zero, FpE.One.Negative());
        return new Banderwagon(affinePoint.X, affinePoint.Y);
    }

    public static Banderwagon operator +(in Banderwagon a, in Banderwagon b)
    {
        return Add(a, b);
    }

    public static Banderwagon operator -(in Banderwagon a, in Banderwagon b)
    {
        return Sub(a, b);
    }

    public static Banderwagon operator *(in Banderwagon a, in FrE b)
    {
        return ScalarMul(a, b);
    }

    public static Banderwagon operator *(in FrE a, in Banderwagon b)
    {
        return ScalarMul(b, a);
    }

    public static bool operator ==(in Banderwagon a, in Banderwagon b)
    {
        return Equals(a, b);
    }

    public static bool operator !=(in Banderwagon a, in Banderwagon b)
    {
        return !(a == b);
    }

    public static bool Equals(Banderwagon x, Banderwagon y)
    {
        FpE x1 = x.X;
        FpE y1 = x.Y;
        FpE x2 = y.X;
        FpE y2 = y.Y;

        if (x1.IsZero && y1.IsZero) return false;

        if (x2.IsZero && y2.IsZero) return false;

        FpE lhs = x1 * y2;
        FpE rhs = x2 * y1;

        return lhs.Equals(rhs);
    }

    private bool Equals(Banderwagon other)
    {
        return Equals(this, other);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        return obj.GetType() == GetType() && Equals((Banderwagon)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Z);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowInvalidConstraintException() =>
        throw new InvalidConstraintException("Z cannot be zero when converting to Affine");
}
