// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm;

using Int256;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Computes the Keccak-256 hash of a specified memory region.
    /// Pops a memory offset and length from the stack, charges gas based on the data size,
    /// and pushes the resulting 256-bit hash onto the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionKeccak256<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        // Ensure two 256-bit words are available (memory offset and length).
        if (!stack.PopUInt256(out UInt256 a) || !stack.PopUInt256(out UInt256 b))
            goto StackUnderflow;

        // Deduct gas: base cost plus additional cost per 32-byte word.
        gasAvailable -= GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in b, out bool outOfGas);
        if (outOfGas)
            goto OutOfGas;

        EvmState vmState = vm.EvmState;
        // Charge gas for any required memory expansion.
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, b))
            goto OutOfGas;

        // Load the target memory region.
        Span<byte> bytes = vmState.Memory.LoadSpan(in a, b);
        // Compute the Keccak-256 hash.
        KeccakCache.ComputeTo(bytes, out ValueHash256 keccak);
        // Push the 256-bit hash result onto the stack.
        stack.Push32Bytes<TTracingInst>(in keccak);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
