// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Evm;

using static Nethermind.Evm.VirtualMachine;
using Int256;

internal sealed partial class EvmInstructions
{
    [SkipLocalsInit]
    public static EvmExceptionType InstructionGas(VirtualMachine _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Base;

        // Ensure gas is positive before pushing to stack
        if (gasAvailable < 0) goto OutOfGas;

        stack.PushUInt64((ulong)gasAvailable);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlobHash(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;

        gasAvailable -= GasCostOf.BlobHash;

        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        byte[][] versionedHashes = vm.EvmState.Env.TxExecutionContext.BlobVersionedHashes;

        if (versionedHashes is not null && result < versionedHashes.Length)
        {
            stack.PushBytes(versionedHashes[result.u0]);
        }
        else
        {
            stack.PushZero();
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlockHash(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.BlockHash;

        if (!stack.PopUInt256(out UInt256 a)) goto StackUnderflow;
        long number = a > long.MaxValue ? long.MaxValue : (long)a;

        Hash256? blockHash = vm.BlockHashProvider.GetBlockhash(vm.EvmState.Env.TxExecutionContext.BlockExecutionContext.Header, number);

        stack.PushBytes(blockHash is not null ? blockHash.Bytes : BytesZero32);

        if (vm.TxTracer.IsTracingBlockHash && blockHash is not null)
        {
            vm.TxTracer.ReportBlockHash(blockHash);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
