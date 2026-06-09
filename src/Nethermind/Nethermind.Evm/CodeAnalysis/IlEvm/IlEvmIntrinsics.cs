// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Int256;
using SignedInt256 = Nethermind.Int256.Int256;

namespace Nethermind.Evm.CodeAnalysis.IlEvm;

/// <summary>
/// Value-form implementations of EVM opcodes whose interpreter handlers carry semantics
/// outside their <c>Op*.Operation</c> structs (range guards, sign handling, byte addressing).
/// Each method mirrors its handler line for line — the handler is the reference
/// implementation, and the differential tests hold the two together.
/// All methods are allocation-free and called from emitted IL like the UInt256 primitives.
/// </summary>
public static class IlEvmIntrinsics
{
    /// <summary>Mirrors InstructionShift&lt;OpShl&gt;: shift ≥ 256 yields zero.</summary>
    public static void Shl(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        if (!a.IsUint64 || a.u0 >= 256)
        {
            result = default;
            return;
        }

        result = b << (int)a.u0;
    }

    /// <summary>Mirrors InstructionShift&lt;OpShr&gt;: shift ≥ 256 yields zero.</summary>
    public static void Shr(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        if (!a.IsUint64 || a.u0 >= 256)
        {
            result = default;
            return;
        }

        result = b >> (int)a.u0;
    }

    /// <summary>Mirrors InstructionSar: arithmetic shift with sign-dependent saturation at ≥ 256.</summary>
    public static void Sar(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        if (!a.IsUint64 || a.u0 >= 256)
        {
            result = Unsafe.As<UInt256, SignedInt256>(ref Unsafe.AsRef(in b)).Sign >= 0
                ? default
                : UInt256.MaxValue; // Int256.MinusOne reinterpreted as unsigned
            return;
        }

        Unsafe.As<UInt256, SignedInt256>(ref Unsafe.AsRef(in b)).RightShift((int)a.u0, out SignedInt256 shifted);
        result = Unsafe.As<SignedInt256, UInt256>(ref shifted);
    }

    /// <summary>Mirrors InstructionSignExtend: extends the sign of the byte at index <paramref name="a"/> (0 = least significant).</summary>
    public static void SignExtend(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        if (!a.IsUint64 || a.u0 >= 32)
        {
            result = b;
            return;
        }

        Span<byte> bytes = stackalloc byte[32];
        b.ToBigEndian(bytes);
        int position = 31 - (int)a.u0;
        sbyte sign = (sbyte)bytes[position];
        bytes[..position].Fill(sign < 0 ? (byte)0xFF : (byte)0x00);
        result = new UInt256(bytes, isBigEndian: true);
    }

    /// <summary>Mirrors InstructionByte: byte at big-endian index <paramref name="a"/> (0 = most significant), out of range yields zero.</summary>
    public static void Byte(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        if (!a.IsUint64 || a.u0 >= 32)
        {
            result = default;
            return;
        }

        Span<byte> bytes = stackalloc byte[32];
        b.ToBigEndian(bytes);
        result = bytes[(int)a.u0];
    }

    /// <summary>Mirrors OpNot: ones' complement.</summary>
    public static void Not(in UInt256 a, out UInt256 result) => result = ~a;

    /// <summary>Mirrors InstructionMath3Param&lt;OpAddMod&gt;: a zero modulus yields zero (UInt256.AddMod would throw).</summary>
    public static void AddMod(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result)
    {
        if (c.IsZero)
        {
            result = default;
            return;
        }

        EvmInstructions.OpAddMod.Operation(in a, in b, in c, out result);
    }

    /// <summary>Mirrors InstructionMath3Param&lt;OpMulMod&gt;: a zero modulus yields zero (UInt256.MultiplyMod would throw).</summary>
    public static void MulMod(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result)
    {
        if (c.IsZero)
        {
            result = default;
            return;
        }

        EvmInstructions.OpMulMod.Operation(in a, in b, in c, out result);
    }
}
