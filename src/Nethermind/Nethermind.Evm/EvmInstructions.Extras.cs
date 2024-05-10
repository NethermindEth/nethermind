// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    [SkipLocalsInit]
    public static EvmExceptionType InstructionGas(EvmState vmState, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Base;

        // Ensure gas is positive before pushing to stack
        if (gasAvailable < 0) return EvmExceptionType.OutOfGas;

        stack.PushUInt256((UInt256)gasAvailable);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlobHash(EvmState vmState, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vmState.Spec;
        if (!spec.IsEip4844Enabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= GasCostOf.BlobHash;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;

        byte[][] versionedHashes = vmState.Env.TxExecutionContext.BlobVersionedHashes;

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
    public static EvmExceptionType InstructionBlobBaseFee(EvmState vmState, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        ref readonly BlockExecutionContext blockContext = ref vmState.Env.TxExecutionContext.BlockExecutionContext;
        IReleaseSpec spec = vmState.Spec;
        if (!spec.BlobBaseFeeEnabled || !blockContext.BlobBaseFee.HasValue) return EvmExceptionType.BadInstruction;

        gasAvailable -= GasCostOf.Base;

        stack.PushUInt256(blockContext.BlobBaseFee.Value);

        return EvmExceptionType.None;
    }
}
