// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions;

using Word = Vector256<byte>;

public static class UInt256Extensions
{
    // value?.IsZero == false <=> x > 0
    public static bool IsPositive(this UInt256? @this) => @this?.IsZero == false;

    [SkipLocalsInit]
    public static ValueHash256 ToValueHash(this in UInt256 value)
    {
        Unsafe.SkipInit(out Word result);
        if (Avx2.IsSupported)
        {
            Word shuffle = Vector256.Create(
                0x18191a1b1c1d1e1ful,
                0x1011121314151617ul,
                0x08090a0b0c0d0e0ful,
                0x0001020304050607ul).AsByte();
            if (Avx512Vbmi.VL.IsSupported)
            {
                Word data = Unsafe.As<UInt256, Word>(ref Unsafe.AsRef(in value));
                result = Avx512Vbmi.VL.PermuteVar32x8(data, shuffle);
            }
            else if (Avx2.IsSupported)
            {
                Vector256<ulong> permute = Unsafe.As<UInt256, Vector256<ulong>>(ref Unsafe.AsRef(in value));
                Vector256<ulong> convert = Avx2.Permute4x64(permute, 0b_01_00_11_10);
                result = Avx2.Shuffle(Unsafe.As<Vector256<ulong>, Word>(ref convert), shuffle);
            }
        }
        else
        {
            ulong u3, u2, u1, u0;
            if (BitConverter.IsLittleEndian)
            {
                u3 = BinaryPrimitives.ReverseEndianness(value.u3);
                u2 = BinaryPrimitives.ReverseEndianness(value.u2);
                u1 = BinaryPrimitives.ReverseEndianness(value.u1);
                u0 = BinaryPrimitives.ReverseEndianness(value.u0);
            }
            else
            {
                u3 = value.u3;
                u2 = value.u2;
                u1 = value.u1;
                u0 = value.u0;
            }

            result = Vector256.Create(u3, u2, u1, u0).AsByte();
        }

        return Unsafe.As<Word, ValueHash256>(ref result);
    }
}
