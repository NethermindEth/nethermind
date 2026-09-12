// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static partial class EvmInstructions
{
    /// <summary>
    /// Runs the scratch-space hash — <c>MSTORE, PUSH1 0x20, MSTORE, PUSH1 0x40, &lt;zero push&gt;,
    /// KECCAK256</c> — as one step, leaving the program counter past the run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both stores are still performed. The scratch space is the compiler's to reuse, but nothing proves
    /// later code will not read it, so only the dispatch and stack traffic are saved, never the writes.
    /// </para>
    /// <para>
    /// Gas is taken through the same primitives the separate opcodes use, in the order they would run, so
    /// the charge cannot drift from the dispatch loop's. The run is declined when the stack is too shallow
    /// to feed it, leaving the ordinary MSTORE to reach the same underflow the loop would.
    /// </para>
    /// </remarks>
    public static EvmExceptionType InstructionMappingSlotKeccak<TGasPolicy, TTracingInst>(
        ref EvmStack stack,
        ref TGasPolicy gas,
        VirtualMachine<TGasPolicy> vm,
        ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Two words for the leading MSTORE and one for the second; anything less and the run cannot stand
        // in for the opcodes it replaces.
        if (stack.Head < 3)
            return InstructionMStore<TGasPolicy, TTracingInst>(ref stack, ref gas, vm);

        nint site = programCounter - 1;
        ReadOnlySpan<byte> code = MemoryMarshal.CreateReadOnlySpan(ref stack.Code, (int)stack.CodeLength);
        bool usesPush0 = MappingSlotFusion.UsesPush0(code, site);

        VmState<TGasPolicy> vmState = vm.VmState;

        // MSTORE: the only operand the run takes from the stack rather than the code.
        if (!TGasPolicy.UpdateGas<VeryLowGasCost>(ref gas)) return EvmExceptionType.OutOfGas;
        if (!stack.PopMemoryPositionAndWord256(out UInt256 keyOffset, out Span<byte> key)) return EvmExceptionType.StackUnderflow;
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in keyOffset, WordSize, ref vmState.Memory)) return EvmExceptionType.OutOfGas;
        vmState.Memory.StoreWordAfterGas(in keyOffset, key);

        // PUSH1 0x20, then MSTORE of the word beneath.
        if (!TGasPolicy.UpdateGas<VeryLowGasCost>(ref gas)) return EvmExceptionType.OutOfGas;
        if (!TGasPolicy.UpdateGas<VeryLowGasCost>(ref gas)) return EvmExceptionType.OutOfGas;
        Span<byte> slot = stack.PopWord256();
        UInt256 slotOffset = (UInt256)MappingSlotFusion.SecondWordOffset;
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in slotOffset, WordSize, ref vmState.Memory)) return EvmExceptionType.OutOfGas;
        vmState.Memory.StoreWordAfterGas(in slotOffset, slot);

        // PUSH1 0x40, then the trailing zero in whichever form the code uses.
        if (!TGasPolicy.UpdateGas<VeryLowGasCost>(ref gas)) return EvmExceptionType.OutOfGas;
        bool zeroPushed = usesPush0
            ? TGasPolicy.UpdateGas<BaseGasCost>(ref gas)
            : TGasPolicy.UpdateGas<VeryLowGasCost>(ref gas);
        if (!zeroPushed) return EvmExceptionType.OutOfGas;

        // KECCAK256 over the scratch space.
        if (!TGasPolicy.TryConsumeKeccak(ref gas, ScratchWords)) return EvmExceptionType.OutOfGas;
        UInt256 scratchOffset = UInt256.Zero;
        UInt256 scratchSize = (UInt256)MappingSlotFusion.ScratchSize;
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in scratchOffset, in scratchSize, ref vmState.Memory) ||
            !vmState.Memory.TryLoadSpan(in scratchOffset, in scratchSize, out Span<byte> scratch))
        {
            return EvmExceptionType.OutOfGas;
        }

        KeccakCache.ComputeTo(scratch, out ValueHash256 keccak);

        programCounter = MappingSlotFusion.EndOf(code, site);
        return stack.Push32Bytes<TTracingInst, OnFlag>(in keccak);
    }

    /// <summary>The scratch space is two words, so KECCAK256 is always charged for two.</summary>
    private const ulong ScratchWords = MappingSlotFusion.ScratchSize / WordSize;

    private const int WordSize = EvmStack.WordSize;
}
