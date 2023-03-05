// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls.Shamatar;
using Nethermind.Evm.Precompiles.Snarks.Shamatar;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using System.Runtime.Intrinsics;

namespace Nethermind.Evm;
public partial class VirtualMachine
{
    private static void InstructionSHA3(ref EvmStack stack, EvmState vmState, in UInt256 memSrc, in UInt256 memLength)
    {
        Span<byte> memData = vmState.Memory.LoadSpan(in memSrc, memLength);
        stack.PushBytes(ValueKeccak.Compute(memData).BytesAsSpan);
    }

    private static void InstructionBYTE(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 position);
        Span<byte> bytes = stack.PopBytes();

        if (position >= BigInt32)
        {
            stack.PushZero();
            return;
        }

        int adjustedPosition = bytes.Length - 32 + (int)position;
        if (adjustedPosition < 0)
        {
            stack.PushZero();
        }
        else
        {
            stack.PushByte(bytes[adjustedPosition]);
        }
    }

    private static void InstructionNOT(ref EvmStack stack)
    {
        Span<byte> a = stack.PopBytes();

        Vector256<byte> aVec = MemoryMarshal.Read<Vector256<byte>>(a);
        MemoryMarshal.AsRef<Vector256<byte>>(stack.Register) = ~aVec;

        stack.PushBytes(stack.Register);
    }

    private static void InstructionXOR(ref EvmStack stack)
    {
        Span<byte> a = stack.PopBytes();
        Span<byte> b = stack.PopBytes();

        Vector256<byte> aVec = MemoryMarshal.Read<Vector256<byte>>(a);
        Vector256<byte> bVec = MemoryMarshal.Read<Vector256<byte>>(b);
        MemoryMarshal.AsRef<Vector256<byte>>(stack.Register) = aVec ^ bVec;

        stack.PushBytes(stack.Register);
    }

    private static void InstructionOR(ref EvmStack stack)
    {
        Span<byte> a = stack.PopBytes();
        Span<byte> b = stack.PopBytes();

        Vector256<byte> aVec = MemoryMarshal.Read<Vector256<byte>>(a);
        Vector256<byte> bVec = MemoryMarshal.Read<Vector256<byte>>(b);
        MemoryMarshal.AsRef<Vector256<byte>>(stack.Register) = aVec | bVec;

        stack.PushBytes(stack.Register);
    }

    private static void InstructionAND(ref EvmStack stack)
    {
        Span<byte> a = stack.PopBytes();
        Span<byte> b = stack.PopBytes();

        Vector256<byte> aVec = MemoryMarshal.Read<Vector256<byte>>(a);
        Vector256<byte> bVec = MemoryMarshal.Read<Vector256<byte>>(b);
        MemoryMarshal.AsRef<Vector256<byte>>(stack.Register) = aVec & bVec;

        stack.PushBytes(stack.Register);
    }

    private static void InstructionISZERO(ref EvmStack stack)
    {
        Span<byte> a = stack.PopBytes();
        if (a.SequenceEqual(BytesZero32))
        {
            stack.PushOne();
        }
        else
        {
            stack.PushZero();
        }
    }

    private static void InstructionEQ(ref EvmStack stack)
    {
        Span<byte> a = stack.PopBytes();
        Span<byte> b = stack.PopBytes();
        if (a.SequenceEqual(b))
        {
            stack.PushOne();
        }
        else
        {
            stack.PushZero();
        }
    }

    private static void InstructionSGT(ref EvmStack stack)
    {
        stack.PopSignedInt256(out Int256.Int256 a);
        stack.PopSignedInt256(out Int256.Int256 b);
        if (a.CompareTo(b) > 0)
        {
            stack.PushOne();
        }
        else
        {
            stack.PushZero();
        }
    }

    private static void InstructionSLT(ref EvmStack stack)
    {
        stack.PopSignedInt256(out Int256.Int256 a);
        stack.PopSignedInt256(out Int256.Int256 b);

        if (a.CompareTo(b) < 0)
        {
            stack.PushOne();
        }
        else
        {
            stack.PushZero();
        }
    }

    private static void InstructionGT(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 a);
        stack.PopUInt256(out UInt256 b);
        if (a > b)
        {
            stack.PushOne();
        }
        else
        {
            stack.PushZero();
        }
    }

    private static void InstructionLT(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 a);
        stack.PopUInt256(out UInt256 b);
        if (a < b)
        {
            stack.PushOne();
        }
        else
        {
            stack.PushZero();
        }
    }

    private static void InstructionSIGNEXTEND(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 a);
        if (a >= BigInt32)
        {
            stack.EnsureDepth(1);
            return;
        }
        int position = 31 - (int)a;

        Span<byte> b = stack.PopBytes();
        sbyte sign = (sbyte)b[position];

        if (sign >= 0)
        {
            b[..position].Clear();
        }
        else
        {
            b[..position].Fill(byte.MaxValue);
        }

        stack.PushBytes(b);
    }

    private static bool InstructionEXP(ref EvmStack stack, ref long gasAvailable, IReleaseSpec spec)
    {
        Metrics.ModExpOpcode++;

        stack.PopUInt256(out UInt256 baseInt);
        Span<byte> exp = stack.PopBytes();

        int leadingZeros = exp.LeadingZerosCount();
        if (leadingZeros != 32)
        {
            int expSize = 32 - leadingZeros;
            if (!UpdateGas(spec.GetExpByteCost() * expSize, ref gasAvailable)) return false;
        }
        else
        {
            stack.PushOne();
            return true;
        }

        if (baseInt.IsZero)
        {
            stack.PushZero();
        }
        else if (baseInt.IsOne)
        {
            stack.PushOne();
        }
        else
        {
            UInt256.Exp(baseInt, new UInt256(exp, true), out UInt256 res);
            stack.PushUInt256(in res);
        }

        return true;
    }

    private static void InstructionMULMOD(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 a);
        stack.PopUInt256(out UInt256 b);
        stack.PopUInt256(out UInt256 mod);

        if (mod.IsZero)
        {
            stack.PushZero();
        }
        else
        {
            UInt256.MultiplyMod(in a, in b, in mod, out UInt256 res);
            stack.PushUInt256(in res);
        }
    }

    private static void InstructionADDMOD(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 a);
        stack.PopUInt256(out UInt256 b);
        stack.PopUInt256(out UInt256 mod);

        if (mod.IsZero)
        {
            stack.PushZero();
        }
        else
        {
            UInt256.AddMod(a, b, mod, out UInt256 res);
            stack.PushUInt256(in res);
        }
    }

    private static void InstructionSMOD(ref EvmStack stack)
    {
        stack.PopSignedInt256(out Int256.Int256 a);
        stack.PopSignedInt256(out Int256.Int256 b);
        if (b.IsZero || b.IsOne)
        {
            stack.PushZero();
        }
        else
        {
            a.Mod(in b, out Int256.Int256 mod);
            stack.PushSignedInt256(in mod);
        }
    }

    private static void InstructionMOD(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 a);
        stack.PopUInt256(out UInt256 b);
        UInt256.Mod(in a, in b, out UInt256 result);
        stack.PushUInt256(in result);
    }

    private static void InstructionDIV(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 a);
        stack.PopUInt256(out UInt256 b);
        if (b.IsZero)
        {
            stack.PushZero();
        }
        else
        {
            UInt256.Divide(in a, in b, out UInt256 res);
            stack.PushUInt256(in res);
        }
    }

    private static void InstructionSDIV(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 a);
        stack.PopSignedInt256(out Int256.Int256 b);
        if (b.IsZero)
        {
            stack.PushZero();
        }
        else if (b == Int256.Int256.MinusOne && a == P255)
        {
            UInt256 res = P255;
            stack.PushUInt256(in res);
        }
        else
        {
            Int256.Int256 signedA = new(a);
            Int256.Int256.Divide(in signedA, in b, out Int256.Int256 res);
            stack.PushSignedInt256(in res);
        }
    }

    private static void InstructionSUB(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 a);
        stack.PopUInt256(out UInt256 b);
        UInt256.Subtract(in a, in b, out UInt256 result);

        stack.PushUInt256(in result);
    }

    private static void InstructionMUL(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 a);
        stack.PopUInt256(out UInt256 b);
        UInt256.Multiply(in a, in b, out UInt256 res);
        stack.PushUInt256(in res);
    }

    private static void InstructionADD(ref EvmStack stack)
    {
        stack.PopUInt256(out UInt256 b);
        stack.PopUInt256(out UInt256 a);
        UInt256.Add(in a, in b, out UInt256 c);
        stack.PushUInt256(c);
    }
}
