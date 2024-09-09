// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;

namespace Nethermind.Evm;
using Int256;

using Nethermind.Core.Crypto;
using static Nethermind.Evm.VirtualMachine;

internal sealed partial class EvmInstructions
{
    [SkipLocalsInit]
    public static EvmExceptionType InstructionGas(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Base;

        // Ensure gas is positive before pushing to stack
        if (gasAvailable < 0) return EvmExceptionType.OutOfGas;

        stack.PushUInt256((UInt256)gasAvailable);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlobHash(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        if (!spec.IsEip4844Enabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= GasCostOf.BlobHash;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;

        byte[][] versionedHashes = vm.State.Env.TxExecutionContext.BlobVersionedHashes;

        if (versionedHashes is not null && result < versionedHashes.Length)
        {
            stack.PushBytes(versionedHashes[result.u0]);
        }
        else
        {
            stack.PushZero();
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlockHash(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        Metrics.BlockhashOpcode++;

        gasAvailable -= GasCostOf.BlockHash;

        if (!stack.PopUInt256(out var a)) return EvmExceptionType.StackUnderflow;
        long number = a > long.MaxValue ? long.MaxValue : (long)a;

        Hash256? blockHash = vm.BlockhashProvider.GetBlockhash(vm.State.Env.TxExecutionContext.BlockExecutionContext.Header, number);

        stack.PushBytes(blockHash is not null ? blockHash.Bytes : BytesZero32);

        if (vm.TxTracer.IsTracingBlockHash && blockHash is not null)
        {
            vm.TxTracer.ReportBlockHash(blockHash);
        }

        return EvmExceptionType.None;
    }
}
