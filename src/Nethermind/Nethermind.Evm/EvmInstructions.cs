// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using static Nethermind.Evm.VirtualMachine;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    [SkipLocalsInit]
    public static void InstructionPushN(ref EvmStack stack, ref long gasAvailable, ref int programCounter, Instruction instruction, ReadOnlySpan<byte> code)
    {
        gasAvailable -= GasCostOf.VeryLow;

        int length = instruction - Instruction.PUSH1 + 1;
        int usedFromCode = Math.Min(code.Length - programCounter, length);
        stack.PushLeftPaddedBytes(code.Slice(programCounter, usedFromCode), length);

        programCounter += length;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionPrevRandao(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        gasAvailable -= GasCostOf.Base;
        BlockHeader header = vmState.Env.TxExecutionContext.BlockExecutionContext.Header;
        if (header.IsPostMerge)
        {
            stack.PushBytes(header.Random.Bytes);
        }
        else
        {
            UInt256 result = header.Difficulty;
            stack.PushUInt256(in result);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionRevert(EvmState vmState, ref EvmStack stack, ref long gasAvailable, out object returnData)
    {
        SkipInit(out returnData);

        IReleaseSpec spec = vmState.Spec;
        if (!spec.RevertOpcodeEnabled) return EvmExceptionType.BadInstruction;

        if (!stack.PopUInt256(out UInt256 position) ||
            !stack.PopUInt256(out UInt256 length))
            return EvmExceptionType.StackUnderflow;

        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, in length))
        {
            return EvmExceptionType.OutOfGas;
        }

        returnData = vmState.Memory.Load(in position, in length).ToArray();
        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturn(EvmState vmState, ref EvmStack stack, ref long gasAvailable, out object returnData)
    {
        SkipInit(out returnData);

        if (!stack.PopUInt256(out UInt256 position) ||
            !stack.PopUInt256(out UInt256 length))
            return EvmExceptionType.StackUnderflow;

        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, in length))
        {
            return EvmExceptionType.OutOfGas;
        }

        returnData = vmState.Memory.Load(in position, in length).ToArray();

        return EvmExceptionType.None;
    }

    public static EvmExceptionType InstructionBadInstruction(ref EvmStack stack, ref long gasAvailable, IReleaseSpec spec)
        => EvmExceptionType.BadInstruction;
    public static EvmExceptionType InstructionBadInstruction(EvmState _, ref EvmStack stack, ref long gasAvailable)
        => EvmExceptionType.BadInstruction;

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExp(ref EvmStack stack, ref long gasAvailable, IReleaseSpec spec)
    {
        gasAvailable -= GasCostOf.Exp;

        Metrics.ModExpOpcode++;

        if (!stack.PopUInt256(out var a)) return EvmExceptionType.StackUnderflow;
        Span<byte> bytes = stack.PopWord256();

        int leadingZeros = bytes.LeadingZerosCount();
        if (leadingZeros == 32)
        {
            stack.PushOne();
        }
        else
        {
            int expSize = 32 - leadingZeros;
            gasAvailable -= spec.GetExpByteCost() * expSize;

            if (a.IsZero)
            {
                stack.PushZero();
            }
            else if (a.IsOne)
            {
                stack.PushOne();
            }
            else
            {
                UInt256.Exp(a, new UInt256(bytes, true), out UInt256 result);
                stack.PushUInt256(in result);
            }
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionByte(ref EvmStack stack, ref long gasAvailable, IReleaseSpec _)
    {
        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out var a)) return EvmExceptionType.StackUnderflow;
        Span<byte> bytes = stack.PopWord256();

        if (a >= BigInt32)
        {
            stack.PushZero();
        }
        else
        {
            int adjustedPosition = bytes.Length - 32 + (int)a;
            if (adjustedPosition < 0)
            {
                stack.PushZero();
            }
            else
            {
                stack.PushByte(bytes[adjustedPosition]);
            }
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionSignExtend(ref EvmStack stack, ref long gasAvailable, IReceiptSpec _)
    {
        gasAvailable -= GasCostOf.Low;

        if (!stack.PopUInt256(out var a)) return EvmExceptionType.StackUnderflow;
        if (a >= BigInt32)
        {
            if (!stack.EnsureDepth(1)) return EvmExceptionType.StackUnderflow;
            return EvmExceptionType.None;
        }

        int position = 31 - (int)a;

        Span<byte> bytes = stack.PeekWord256();
        sbyte sign = (sbyte)bytes[position];

        if (sign >= 0)
        {
            BytesZero32.AsSpan(0, position).CopyTo(bytes[..position]);
        }
        else
        {
            BytesMax32.AsSpan(0, position).CopyTo(bytes[..position]);
        }

        // Didn't remove from stack so don't need to push back
        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionKeccak256(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        gasAvailable -= GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in b);

        if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, b)) return EvmExceptionType.OutOfGas;

        Span<byte> bytes = vmState.Memory.LoadSpan(in a, b);
        stack.PushBytes(ValueKeccak.Compute(bytes).BytesAsSpan);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionCallDataLoad(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        stack.PushBytes(vmState.Env.InputData.SliceWithZeroPadding(result, 32));

        return EvmExceptionType.None;
    }


    public static bool UpdateMemoryCost(EvmState vmState, ref long gasAvailable, in UInt256 position, in UInt256 length)
    {
        long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length);
        if (memoryCost != 0L)
        {
            if (!UpdateGas(memoryCost, ref gasAvailable))
            {
                return false;
            }
        }

        return true;
    }

    public static bool UpdateGas(long gasCost, ref long gasAvailable)
    {
        if (gasAvailable < gasCost)
        {
            return false;
        }

        gasAvailable -= gasCost;
        return true;
    }

    public static void UpdateGasUp(long refund, ref long gasAvailable)
    {
        gasAvailable += refund;
    }
}
