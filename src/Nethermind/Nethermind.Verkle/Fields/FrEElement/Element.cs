// Copyright 2022 Demerzel Solutions Limited
// Licensed under Apache-2.0. For full terms, see LICENSE in the project root.

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Field.Montgomery.Test")]

namespace Nethermind.Verkle.Fields.FrEElement;

/// <summary>
///     This is the scalar field associated with the bandersnatch curve
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public readonly partial struct FrE
{
    /* in little endian order so u3 is the most significant ulong */
    [FieldOffset(00)] public readonly ulong u0;
    [FieldOffset(08)] public readonly ulong u1;
    [FieldOffset(16)] public readonly ulong u2;
    [FieldOffset(24)] public readonly ulong u3;

    private ulong this[int index] => index switch
    {
        0 => u0,
        1 => u1,
        2 => u2,
        3 => u3,
        _ => ThrowIndexOutOfRangeException()
    };

    public bool IsZero
    {
        get
        {
            if (Avx.IsSupported)
            {
                Vector256<ulong> v = Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.AsRef(in u0));
                return Avx.TestZ(v, v);
            }

            return (u0 | u1 | u2 | u3) == 0;
        }
    }

    public bool IsOne => Equals(One);

    public bool IsRegularOne
    {
        get
        {
            if (Avx.IsSupported)
            {
                Vector256<ulong> v = Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.AsRef(in u0));
                return v == Vector256.CreateScalar(1UL);
            }

            return ((u0 ^ 1UL) | u1 | u2 | u3) == 0;
        }
    }

    public FrE(ulong u0 = 0, ulong u1 = 0, ulong u2 = 0, ulong u3 = 0)
    {
        if (Avx2.IsSupported)
        {
            Unsafe.SkipInit(out this.u0);
            Unsafe.SkipInit(out this.u1);
            Unsafe.SkipInit(out this.u2);
            Unsafe.SkipInit(out this.u3);
            Unsafe.As<ulong, Vector256<ulong>>(ref this.u0) = Vector256.Create(u0, u1, u2, u3);
        }
        else
        {
            this.u0 = u0;
            this.u1 = u1;
            this.u2 = u2;
            this.u3 = u3;
        }
    }

    public FrE(in ReadOnlySpan<byte> bytes, bool isBigEndian = false)
    {
        if (bytes.Length == 32)
        {
            if (isBigEndian)
            {
                u3 = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(0, 8));
                u2 = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8));
                u1 = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(16, 8));
                u0 = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(24, 8));
            }
            else
            {
                if (Avx2.IsSupported)
                {
                    Unsafe.SkipInit(out u0);
                    Unsafe.SkipInit(out u1);
                    Unsafe.SkipInit(out u2);
                    Unsafe.SkipInit(out u3);
                    Unsafe.As<ulong, Vector256<byte>>(ref u0) = Vector256.Create(bytes);
                }
                else
                {
                    u0 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(0, 8));
                    u1 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8));
                    u2 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(16, 8));
                    u3 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24, 8));
                }
            }
        }
        else
            FieldUtils.Create(bytes, out u0, out u1, out u2, out u3);
    }

    private FrE(BigInteger value)
    {
        UInt256 res;
        if (value.Sign < 0)
            SubtractMod(UInt256.Zero, (UInt256)(-value), _modulus.Value, out res);
        else
            UInt256.Mod((UInt256)value, _modulus.Value, out res);

        u0 = res.u0;
        u1 = res.u1;
        u2 = res.u2;
        u3 = res.u3;
    }
}
