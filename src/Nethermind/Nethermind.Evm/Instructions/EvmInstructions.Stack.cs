// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm;
using Int256;

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
    public struct Op17 : IOpCount { public static int Count => 17; }
    public struct Op18 : IOpCount { public static int Count => 18; }
    public struct Op19 : IOpCount { public static int Count => 19; }
    public struct Op20 : IOpCount { public static int Count => 20; }
    public struct Op21 : IOpCount { public static int Count => 21; }
    public struct Op22 : IOpCount { public static int Count => 22; }
    public struct Op23 : IOpCount { public static int Count => 23; }
    public struct Op24 : IOpCount { public static int Count => 24; }
    public struct Op25 : IOpCount { public static int Count => 25; }
    public struct Op26 : IOpCount { public static int Count => 26; }
    public struct Op27 : IOpCount { public static int Count => 27; }
    public struct Op28 : IOpCount { public static int Count => 28; }
    public struct Op29 : IOpCount { public static int Count => 29; }
    public struct Op30 : IOpCount { public static int Count => 30; }
    public struct Op31 : IOpCount { public static int Count => 31; }
    public struct Op32 : IOpCount { public static int Count => 32; }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionDup<TOpCount>(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCount : IOpCount
    {
        gasAvailable -= GasCostOf.VeryLow;
        if (!stack.Dup(TOpCount.Count)) return EvmExceptionType.StackUnderflow;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionSwap<TOpCount>(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCount : IOpCount
    {
        gasAvailable -= GasCostOf.VeryLow;
        if (!stack.Swap(TOpCount.Count + 1)) return EvmExceptionType.StackUnderflow;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionPush0(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Base;

        stack.PushZero();

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionPush<TOpCount>(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCount : IOpCount
    {
        gasAvailable -= GasCostOf.VeryLow;

        ReadOnlySpan<byte> code = vm.State.Env.CodeInfo.CodeSection.Span;

        int length = TOpCount.Count;
        int usedFromCode = Math.Min(code.Length - programCounter, length);
        stack.PushLeftPaddedBytes(code.Slice(programCounter, usedFromCode), length);

        programCounter += length;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionLog<TOpCount>(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCount : struct, IOpCount
    {
        EvmState vmState = vm.State;
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

        if (vm.TxTracer.IsTracingLogs)
        {
            vm.TxTracer.ReportLog(logEntry);
        }

        return EvmExceptionType.None;
    }
}
