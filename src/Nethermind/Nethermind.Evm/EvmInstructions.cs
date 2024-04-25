// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.State;
using static Nethermind.Evm.VirtualMachine;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    public static void InstructionPrevRandao<TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable)
        where TTracingInstructions : struct, IIsTracing
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
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionRevert<TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, IReleaseSpec spec, out object returnData)
        where TTracingInstructions : struct, IIsTracing
    {
        SkipInit(out returnData);

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
    public static EvmExceptionType InstructionReturn<TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, out object returnData)
        where TTracingInstructions : struct, IIsTracing
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

    [SkipLocalsInit]
    public static EvmExceptionType InstructionLog<TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, Instruction instruction)
        where TTracingInstructions : struct, IIsTracing
    {
        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;

        if (!stack.PopUInt256(out UInt256 position)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 length)) return EvmExceptionType.StackUnderflow;
        long topicsCount = instruction - Instruction.LOG0;
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, length)) return EvmExceptionType.OutOfGas;
        if (!UpdateGas(
                GasCostOf.Log + topicsCount * GasCostOf.LogTopic +
                (long)length * GasCostOf.LogData, ref gasAvailable))
        {
            return EvmExceptionType.OutOfGas;
        }

        ReadOnlyMemory<byte> data = vmState.Memory.Load(in position, length);
        Hash256[] topics = new Hash256[topicsCount];
        for (int i = 0; i < topicsCount; i++)
        {
            topics[i] = new Hash256(stack.PopWord256());
        }

        LogEntry logEntry = new(
            vmState.Env.ExecutingAccount,
            data.ToArray(),
            topics);
        vmState.Logs.Add(logEntry);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExp<TTracingInstructions>(ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, IReleaseSpec spec)
        where TTracingInstructions : struct, IIsTracing
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
    public static EvmExceptionType InstructionByte<TTracingInstructions>(ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, IReleaseSpec spec)
        where TTracingInstructions : struct, IIsTracing
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
    public static EvmExceptionType InstructionSignExtend<TTracingInstructions>(ref EvmStack<TTracingInstructions> stack, ref long gasAvailable)
        where TTracingInstructions : struct, IIsTracing
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
    public static EvmExceptionType InstructionKeccak256<TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable)
        where TTracingInstructions : struct, IIsTracing
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
    public static EvmExceptionType InstructionCallDataLoad<TTracing>(EvmState vmState, ref EvmStack<TTracing> stack, ref long gasAvailable)
        where TTracing : struct, IIsTracing
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
