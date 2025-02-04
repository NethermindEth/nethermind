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
    /// <summary>
    /// Pushes the remaining gas onto the stack.
    /// The gas available is decremented by the base cost, and if negative, an OutOfGas error is returned.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack where the gas value will be pushed.</param>
    /// <param name="gasAvailable">Reference to the current available gas, which is modified by this operation.</param>
    /// <param name="programCounter">The current program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if gas is available, or <see cref="EvmExceptionType.OutOfGas"/> if the gas becomes negative.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionGas(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Deduct the base gas cost for reading gas.
        gasAvailable -= GasCostOf.Base;

        // If gas falls below zero after cost deduction, signal out-of-gas error.
        if (gasAvailable < 0) goto OutOfGas;

        // Push the remaining gas (as unsigned 64-bit) onto the stack.
        stack.PushUInt64((ulong)gasAvailable);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    }

    /// <summary>
    /// Computes the blob hash from the provided blob versioned hashes.
    /// Pops an index from the stack and uses it to select a blob hash from the versioned hashes array.
    /// If the index is invalid, pushes zero.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack from which the index is popped and where the blob hash is pushed.</param>
    /// <param name="gasAvailable">Reference to the available gas; reduced by the blob hash cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; otherwise, <see cref="EvmExceptionType.StackUnderflow"/>
    /// if there are insufficient elements on the stack.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlobHash(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;

        // Deduct the gas cost for blob hash operation.
        gasAvailable -= GasCostOf.BlobHash;

        // Pop the blob index from the stack.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        // Retrieve the array of versioned blob hashes from the execution context.
        byte[][] versionedHashes = vm.EvmState.Env.TxExecutionContext.BlobVersionedHashes;

        // If versioned hashes are available and the index is within range, push the corresponding blob hash.
        // Otherwise, push zero.
        if (versionedHashes is not null && result < versionedHashes.Length)
        {
            stack.PushBytes(versionedHashes[result.u0]);
        }
        else
        {
            stack.PushZero();
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Retrieves a block hash for a given block number.
    /// Pops a block number from the stack, validates it, and then pushes the corresponding block hash.
    /// If no valid block hash exists, pushes a zero value.
    /// Additionally, reports the block hash if block hash tracing is enabled.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack from which the block number is popped and where the block hash is pushed.</param>
    /// <param name="gasAvailable">Reference to the available gas; reduced by the block hash operation cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully;
    /// otherwise, <see cref="EvmExceptionType.StackUnderflow"/> if there are insufficient stack elements.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlockHash(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Deduct the gas cost for block hash operation.
        gasAvailable -= GasCostOf.BlockHash;

        // Pop the block number from the stack.
        if (!stack.PopUInt256(out UInt256 a)) goto StackUnderflow;

        // Convert the block number to a long. Clamp the value to long.MaxValue if it exceeds it.
        long number = a > long.MaxValue ? long.MaxValue : (long)a.u0;

        // Retrieve the block hash for the given block number.
        Hash256? blockHash = vm.BlockHashProvider.GetBlockhash(vm.EvmState.Env.TxExecutionContext.BlockExecutionContext.Header, number);

        // Push the block hash bytes if available; otherwise, push a 32-byte zero value.
        stack.PushBytes(blockHash is not null ? blockHash.Bytes : BytesZero32);

        // If block hash tracing is enabled and a valid block hash was obtained, report it.
        if (vm.TxTracer.IsTracingBlockHash && blockHash is not null)
        {
            vm.TxTracer.ReportBlockHash(blockHash);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
