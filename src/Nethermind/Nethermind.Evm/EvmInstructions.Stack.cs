// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

using Nethermind.Core;
using Nethermind.Core.Crypto;

internal sealed partial class EvmInstructions
{
    public interface IOpCount
    {
        abstract static int Count { get; }
    }

    public struct Op0 : IOpCount { public static int Count => 0; }
    public struct Op1 : IOpCount { public static int Count => 1; }
    public struct Op2 : IOpCount { public static int Count => 2; }
    public struct Op3 : IOpCount { public static int Count => 3; }
    public struct Op4 : IOpCount { public static int Count => 4; }
    public struct Op5 : IOpCount { public static int Count => 5; }
    public struct Op6 : IOpCount { public static int Count => 6; }
    public struct Op7 : IOpCount { public static int Count => 7; }
    public struct Op8 : IOpCount { public static int Count => 8; }
    public struct Op9 : IOpCount { public static int Count => 9; }
    public struct Op10 : IOpCount { public static int Count => 10; }
    public struct Op11 : IOpCount { public static int Count => 11; }
    public struct Op12 : IOpCount { public static int Count => 12; }
    public struct Op13 : IOpCount { public static int Count => 13; }
    public struct Op14 : IOpCount { public static int Count => 14; }
    public struct Op15 : IOpCount { public static int Count => 15; }
    public struct Op16 : IOpCount { public static int Count => 16; }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionDup<TOpCount>(EvmState _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCount : IOpCount
    {
        gasAvailable -= GasCostOf.VeryLow;
        if (!stack.Dup(TOpCount.Count)) return EvmExceptionType.StackUnderflow;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionSwap<TOpCount>(EvmState _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCount : IOpCount
    {
        gasAvailable -= GasCostOf.VeryLow;
        if (!stack.Swap(TOpCount.Count + 1)) return EvmExceptionType.StackUnderflow;

        return EvmExceptionType.None;
    }
    

    [SkipLocalsInit]
    public static EvmExceptionType InstructionLog<TOpCount>(EvmState vmState, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCount : struct, IOpCount
    {
        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;

        if (!stack.PopUInt256(out UInt256 position)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 length)) return EvmExceptionType.StackUnderflow;
        long topicsCount = TOpCount.Count;
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, length)) return EvmExceptionType.OutOfGas;
        if (!UpdateGas(
                GasCostOf.Log + topicsCount * GasCostOf.LogTopic +
                (long)length * GasCostOf.LogData, ref gasAvailable)) return EvmExceptionType.OutOfGas;

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

        if (vmState.TxTracer.IsTracingLogs)
        {
            vmState.TxTracer.ReportLog(logEntry);
        }

        return EvmExceptionType.None;
    }
}
