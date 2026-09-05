// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    /// <summary>
    /// Computes the Keccak-256 hash of a specified memory region.
    /// Pops a memory offset and length from the stack, charges gas based on the data size,
    /// and pushes the resulting 256-bit hash onto the stack.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionKeccak256<TGasPolicy, TTracingInst>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Ensure two 256-bit words are available (memory offset and length).
        if (!stack.PopMemoryPositionAndUInt256(out UInt256 a, out UInt256 b))
            goto StackUnderflow;

        // Deduct gas: base cost plus additional cost per 32-byte word.
        ulong words = EvmCalculations.Div32Ceiling(in b, out bool outOfGas);
        if (!TGasPolicy.TryConsumeKeccak(ref gas, words)) return EvmExceptionType.OutOfGas;
        if (outOfGas) goto OutOfGas;

        VmState<TGasPolicy> vmState = vm.VmState;
        // Charge gas for any required memory expansion.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in a, b, ref vmState.Memory) ||
            !vmState.Memory.TryLoadSpan(in a, b, out Span<byte> bytes))
        {
            goto OutOfGas;
        }

        // Compute the Keccak-256 hash.
        KeccakCache.ComputeTo(bytes, out ValueHash256 keccak);
        // Push the 256-bit hash result onto the stack.
        return stack.Push32Bytes<TTracingInst>(in keccak);
        // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
