// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;
using static Nethermind.Evm.GuestWord;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The untraced, uncancelable table gets handlers that finish their common case without a call and leave
    /// for every other case through a tail call into the shared handler of the same opcode, which starts it
    /// over from the unchanged arguments. RyuJIT saves the callee-saved registers on every path of a method
    /// that calls anywhere, so keeping the rare cases out of line keeps the common one frameless.
    /// </para>
    /// <para>
    /// The handlers follow the guest's dispatch conventions (see <see cref="RawCalliHelper"/>): they take the stack head
    /// from its argument and hand it on, count no opcodes, and read up to <see cref="CodeInfo.DispatchPadding"/> bytes
    /// past the end of the code, where a fused shape fails to match on a STOP and a push reads zero immediates, as the
    /// EVM pads them. They charge <see cref="EthereumGasPolicy"/>'s constants directly rather than through the policy's
    /// hooks, so no other policy gets them.
    /// </para>
    /// <para>
    /// A fallback names the shared handler by its generic instantiation, which the opcode-naming weaver does not
    /// rename, so the guest carries a second, anonymous copy of each fallback target, and profiles show the time spent
    /// in it under <c>ExecuteOpcode</c>.
    /// </para>
    /// </remarks>
    static partial void ConfigureBuildHandlers<TTracingInst, TCancelable>(
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[] lookup,
        IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        if (TTracingInst.IsActive || TCancelable.IsActive || typeof(TGasPolicy) != typeof(EthereumGasPolicy)) return;

        if (AirbenderFlag.IsActive)
            AirbenderArithmetic.Initialize();

        lookup[(int)Instruction.PUSH1] = AsTableEntry(&RawCalliHelper.ExecutePush1);
        lookup[(int)Instruction.DUP1] = AsTableEntry(&RawCalliHelper.ExecuteDup1);
        lookup[(int)Instruction.POP] = AsTableEntry(&RawCalliHelper.ExecutePop);
        lookup[(int)Instruction.ISZERO] = AsTableEntry(&RawCalliHelper.ExecuteCondition<RawCalliHelper.IsZeroCondition>);
        lookup[(int)Instruction.EQ] = AsTableEntry(&RawCalliHelper.ExecuteCondition<RawCalliHelper.EqualCondition>);
        lookup[(int)Instruction.LT] = AsTableEntry(&RawCalliHelper.ExecuteCondition<RawCalliHelper.LessThanCondition>);
        lookup[(int)Instruction.GT] = AsTableEntry(&RawCalliHelper.ExecuteCondition<RawCalliHelper.GreaterThanCondition>);
        lookup[(int)Instruction.SLT] = AsTableEntry(&RawCalliHelper.ExecuteCondition<RawCalliHelper.SignedLessThanCondition>);
        lookup[(int)Instruction.SGT] = AsTableEntry(&RawCalliHelper.ExecuteCondition<RawCalliHelper.SignedGreaterThanCondition>);
        lookup[(int)Instruction.PUSH2] = AsTableEntry(&RawCalliHelper.ExecutePush2);
        lookup[(int)Instruction.PUSH3] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op3>);
        lookup[(int)Instruction.PUSH4] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op4>);
        lookup[(int)Instruction.PUSH5] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op5>);
        lookup[(int)Instruction.PUSH6] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op6>);
        lookup[(int)Instruction.PUSH7] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op7>);
        lookup[(int)Instruction.PUSH8] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op8>);
        lookup[(int)Instruction.PUSH9] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op9>);
        lookup[(int)Instruction.PUSH10] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op10>);
        lookup[(int)Instruction.PUSH11] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op11>);
        lookup[(int)Instruction.PUSH12] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op12>);
        lookup[(int)Instruction.PUSH13] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op13>);
        lookup[(int)Instruction.PUSH14] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op14>);
        lookup[(int)Instruction.PUSH15] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op15>);
        lookup[(int)Instruction.PUSH16] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op16>);
        lookup[(int)Instruction.PUSH17] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op17>);
        lookup[(int)Instruction.PUSH18] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op18>);
        lookup[(int)Instruction.PUSH19] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op19>);
        lookup[(int)Instruction.PUSH20] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op20>);
        lookup[(int)Instruction.PUSH21] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op21>);
        lookup[(int)Instruction.PUSH22] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op22>);
        lookup[(int)Instruction.PUSH23] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op23>);
        lookup[(int)Instruction.PUSH24] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op24>);
        lookup[(int)Instruction.PUSH25] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op25>);
        lookup[(int)Instruction.PUSH26] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op26>);
        lookup[(int)Instruction.PUSH27] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op27>);
        lookup[(int)Instruction.PUSH28] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op28>);
        lookup[(int)Instruction.PUSH29] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op29>);
        lookup[(int)Instruction.PUSH30] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op30>);
        lookup[(int)Instruction.PUSH31] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op31>);
        lookup[(int)Instruction.PUSH32] = AsTableEntry(&RawCalliHelper.ExecutePush<EvmInstructions.Op32>);
        lookup[(int)Instruction.JUMP] = AsTableEntry(&RawCalliHelper.ExecuteJumpToAnalyzedDestination);
        lookup[(int)Instruction.JUMPI] = AsTableEntry(&RawCalliHelper.ExecuteJumpIfToAnalyzedDestination);
        lookup[(int)Instruction.MLOAD] = AsTableEntry(&RawCalliHelper.ExecuteMLoadFromActiveMemory);
        lookup[(int)Instruction.MSTORE] = AsTableEntry(&RawCalliHelper.ExecuteMStoreInsideBacking);
        lookup[(int)Instruction.MSTORE8] = AsTableEntry(&RawCalliHelper.ExecuteMStore8InsideActiveMemory);
        lookup[(int)Instruction.CALLDATACOPY] = AsTableEntry(&RawCalliHelper.ExecuteDataCopy<RawCalliHelper.CallDataSource>);
        if (SpecFlags.Eip2929(spec) && !SpecFlags.Eip8038(spec))
        {
            lookup[(int)Instruction.SLOAD] = SpecFlags.Eip8360(spec)
                ? AsTableEntry(&RawCalliHelper.ExecuteSLoad<OnFlag>)
                : AsTableEntry(&RawCalliHelper.ExecuteSLoad<OffFlag>);
            if (SpecFlags.NetGasMetering(spec) && SpecFlags.Eip2200(spec) && !SpecFlags.Eip8279(spec) && !SpecFlags.Eip8360(spec))
                lookup[(int)Instruction.SSTORE] = AsTableEntry(&RawCalliHelper.ExecuteSStore);
        }
        if (spec.TransientStorageEnabled)
        {
            lookup[(int)Instruction.TLOAD] = AsTableEntry(&RawCalliHelper.ExecuteTLoad);
            lookup[(int)Instruction.TSTORE] = AsTableEntry(&RawCalliHelper.ExecuteTStore);
        }
        lookup[(int)Instruction.CALLDATALOAD] = AsTableEntry(&RawCalliHelper.ExecuteCallDataLoadOfWholeWord);
        lookup[(int)Instruction.CALLDATASIZE] = AsTableEntry(&RawCalliHelper.ExecutePushValue<RawCalliHelper.CallDataSizeValue>);
        lookup[(int)Instruction.GAS] = AsTableEntry(&RawCalliHelper.ExecutePushValue<RawCalliHelper.GasValue>);
        lookup[(int)Instruction.JUMPDEST] = AsTableEntry(&RawCalliHelper.ExecuteJumpDest);
        if (spec.ReturnDataOpcodesEnabled)
        {
            lookup[(int)Instruction.RETURNDATASIZE] = AsTableEntry(&RawCalliHelper.ExecutePushValue<RawCalliHelper.ReturnDataSizeValue>);
            lookup[(int)Instruction.RETURNDATACOPY] = AsTableEntry(&RawCalliHelper.ExecuteDataCopy<RawCalliHelper.ReturnDataSource>);
        }
        if (spec.IncludePush0Instruction)
            lookup[(int)Instruction.PUSH0] = AsTableEntry(&RawCalliHelper.ExecutePushValue<RawCalliHelper.ZeroValue>);
        lookup[(int)Instruction.KECCAK256] = AsTableEntry(&RawCalliHelper.ExecuteKeccak256OfActiveMemory);
        lookup[(int)Instruction.ADD] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.AddOperation>);
        lookup[(int)Instruction.SUB] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.SubtractOperation>);
        lookup[(int)Instruction.AND] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.AndOperation>);
        lookup[(int)Instruction.OR] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.OrOperation>);
        lookup[(int)Instruction.XOR] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.XorOperation>);
        lookup[(int)Instruction.MUL] = AsTableEntry(&RawCalliHelper.ExecuteMul);
        lookup[(int)Instruction.DIV] = AsTableEntry(&RawCalliHelper.ExecuteDiv);
        lookup[(int)Instruction.ADDMOD] = AsTableEntry(&RawCalliHelper.ExecuteModular<EvmInstructions.OpAddMod>);
        lookup[(int)Instruction.MULMOD] = AsTableEntry(&RawCalliHelper.ExecuteModular<EvmInstructions.OpMulMod>);
        lookup[(int)Instruction.SIGNEXTEND] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.SignExtendOperation>);
        lookup[(int)Instruction.NOT] = AsTableEntry(&RawCalliHelper.ExecuteNot);
        lookup[(int)Instruction.BYTE] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.ByteOperation>);
        lookup[(int)Instruction.DUP2] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op2>);
        lookup[(int)Instruction.DUP3] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op3>);
        lookup[(int)Instruction.DUP4] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op4>);
        lookup[(int)Instruction.DUP5] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op5>);
        lookup[(int)Instruction.DUP6] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op6>);
        lookup[(int)Instruction.DUP7] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op7>);
        lookup[(int)Instruction.DUP8] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op8>);
        lookup[(int)Instruction.DUP9] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op9>);
        lookup[(int)Instruction.DUP10] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op10>);
        lookup[(int)Instruction.DUP11] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op11>);
        lookup[(int)Instruction.DUP12] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op12>);
        lookup[(int)Instruction.DUP13] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op13>);
        lookup[(int)Instruction.DUP14] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op14>);
        lookup[(int)Instruction.DUP15] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op15>);
        lookup[(int)Instruction.DUP16] = AsTableEntry(&RawCalliHelper.ExecuteDup<EvmInstructions.Op16>);
        lookup[(int)Instruction.SWAP1] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op1>);
        lookup[(int)Instruction.SWAP2] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op2>);
        lookup[(int)Instruction.SWAP3] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op3>);
        lookup[(int)Instruction.SWAP4] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op4>);
        lookup[(int)Instruction.SWAP5] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op5>);
        lookup[(int)Instruction.SWAP6] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op6>);
        lookup[(int)Instruction.SWAP7] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op7>);
        lookup[(int)Instruction.SWAP8] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op8>);
        lookup[(int)Instruction.SWAP9] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op9>);
        lookup[(int)Instruction.SWAP10] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op10>);
        lookup[(int)Instruction.SWAP11] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op11>);
        lookup[(int)Instruction.SWAP12] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op12>);
        lookup[(int)Instruction.SWAP13] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op13>);
        lookup[(int)Instruction.SWAP14] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op14>);
        lookup[(int)Instruction.SWAP15] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op15>);
        lookup[(int)Instruction.SWAP16] = AsTableEntry(&RawCalliHelper.ExecuteSwap<EvmInstructions.Op16>);
        if (spec.ShiftOpcodesEnabled)
        {
            lookup[(int)Instruction.SHL] = AsTableEntry(&RawCalliHelper.ExecuteShl);
            lookup[(int)Instruction.SHR] = AsTableEntry(&RawCalliHelper.ExecuteShr);
            lookup[(int)Instruction.SAR] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.ArithmeticShiftRightOperation>);
        }
    }

    private static partial class RawCalliHelper
    {
        private const int LimbsPerWord = EvmStack.WordSize / sizeof(ulong);

        /// <summary>The table entry for <paramref name="handler"/>, a handler with the guest's dispatch signature.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nint Entry(
            delegate*<ref EvmStack, nuint, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType> handler) =>
            (nint)handler;

        /// <summary>Charges <paramref name="cost"/> to <paramref name="gas"/>, or reports that it does not cover it and leaves it unchanged.</summary>
        /// <remarks>
        /// Tests the sign of the charged gas, which is exact for carried gas of at most <see cref="nint.MaxValue"/> (see
        /// <see cref="ChainGas"/>): the charge doubles as the test, one instruction fewer than comparing first, and only
        /// the miss pays it back. On a 32-bit guest a cost past <see cref="int.MaxValue"/> exceeds any carried gas. Gas
        /// that <see cref="ChargeUntested"/> left negative fails the test as well.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryCharge(ref nuint gas, ulong cost)
        {
            if (IntPtr.Size == sizeof(uint) && cost > int.MaxValue) return false;
            gas -= (nuint)cost;
            if ((nint)gas >= 0) return true;
            gas += (nuint)cost;
            return false;
        }

        /// <summary>Charges the fixed <paramref name="cost"/> of an opcode to <paramref name="gas"/> without testing that it covers it.</summary>
        /// <returns>Always <see langword="true"/>, so it takes the place of <see cref="TryCharge"/> in a handler's guard.</returns>
        /// <remarks>
        /// Only for a handler that has no other use of the gas and whose effects stay in the frame. A shortfall leaves the
        /// gas negative, and the next sign test halts the frame out of gas: <see cref="TryCharge"/>, <see cref="Covers"/>,
        /// and the entry of the shared handlers. Every jump tests, so a loop cannot run on a shortfall, and straight-line
        /// code ends at a shared handler: at the latest the STOP that pads the code. A frame that halts on an exception
        /// consumes all its gas and drops its effects whichever opcode raised it, so halting a few opcodes later gives
        /// the same result. The shortfall stays far below 2^31: a code holds fewer than 2^20 opcodes, and every fixed
        /// cost charged this way is below 2^6.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ChargeUntested(ref nuint gas, ulong cost)
        {
            gas -= (nuint)cost;
            return true;
        }

        /// <summary>Whether <paramref name="gas"/> covers <paramref name="cost"/>; negative gas, a shortfall <see cref="ChargeUntested"/> left, covers nothing.</summary>
        /// <remarks>The comparison is signed, so <paramref name="cost"/> must be below 2^31.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Covers(nuint gas, ulong cost)
        {
            Debug.Assert(cost <= int.MaxValue);
            return (nint)gas >= (nint)cost;
        }

        /// <summary>Whether the word at <paramref name="word"/>, in limb layout, is zero.</summary>
        /// <remarks>
        /// A 32-bit guest ORs its eight words in turn, two registers live, where the limb expression keeps four: a branch
        /// handler holding all its arguments has no more temporaries than that.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsZeroWord(ref ulong word)
        {
            if (IntPtr.Size == sizeof(uint))
            {
                ref uint w = ref Unsafe.As<ulong, uint>(ref word);
                uint any = w;
                any |= Unsafe.Add(ref w, 1);
                any |= Unsafe.Add(ref w, 2);
                any |= Unsafe.Add(ref w, 3);
                any |= Unsafe.Add(ref w, 4);
                any |= Unsafe.Add(ref w, 5);
                any |= Unsafe.Add(ref w, 6);
                any |= Unsafe.Add(ref w, 7);
                return any == 0;
            }

            return (word | Unsafe.Add(ref word, 1) | Unsafe.Add(ref word, 2) | Unsafe.Add(ref word, 3)) == 0;
        }

        /// <summary>Whether the word at <paramref name="word"/>, in limb layout, fits a native word, so <c>(nuint)word</c> is all of it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool FitsNativeWord(ref ulong word)
        {
            ulong above = Unsafe.Add(ref word, 1) | Unsafe.Add(ref word, 2) | Unsafe.Add(ref word, 3);
            if (IntPtr.Size == sizeof(uint)) above |= word >> 32;
            return above == 0;
        }

        /// <summary>The slot at <paramref name="index"/>, counted up from <paramref name="bottom"/>, for callers that have bounded the index.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte SlotAt(ref byte bottom, nint index) => ref Unsafe.Add(ref AlignedSlot(ref bottom), index * EvmStack.WordSize);

        /// <summary>The slot <paramref name="depth"/> words below <paramref name="sp"/>, the slot above the top word, for callers that have bounded the depth.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte SlotBelow(ref byte sp, nint depth) => ref Unsafe.Subtract(ref AlignedSlot(ref sp), depth * EvmStack.WordSize);

        /// <summary><paramref name="sp"/> moved by <paramref name="words"/> words, as the head moves by them.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte Moved(ref byte sp, nint words) => ref Unsafe.Add(ref sp, words * EvmStack.WordSize);

        /// <summary><paramref name="slot"/>, rounded down to the word boundary it is already on where <see cref="StrictAlignmentFlag"/> is on.</summary>
        /// <remarks>
        /// The rounding is a no-op the JIT sees through (dotnet-riscv perf-67), so it proves every slot access word aligned and drops its alignment test.
        /// It masks the byte offset from null rather than a pointer: casting the byref to one would copy the head, which stays live, to another register first.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe ref byte AlignedSlot(ref byte slot)
        {
            Debug.Assert(((nuint)Unsafe.AsPointer(ref slot) & (EvmStack.WordSize - 1)) == 0);
            return ref StrictAlignmentFlag.IsActive
                ? ref Unsafe.AddByteOffset(ref Unsafe.NullRef<byte>(), Unsafe.ByteOffset(ref Unsafe.NullRef<byte>(), ref slot) & ~(nint)(EvmStack.WordSize - 1))
                : ref slot;
        }

        /// <summary><paramref name="handlers"/>, rounded down to the word boundary it is already on where <see cref="StrictAlignmentFlag"/> is on.</summary>
        /// <remarks>As <see cref="AlignedSlot"/>: one <c>andi</c> lets the JIT prove the handler load aligned and drop its alignment test.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nint* AlignedTable(nint* handlers)
        {
            Debug.Assert(((nuint)handlers & (nuint)(sizeof(nint) - 1)) == 0);
            return StrictAlignmentFlag.IsActive ? (nint*)((nuint)handlers & ~(nuint)(sizeof(nint) - 1)) : handlers;
        }

        /// <summary><paramref name="stack"/>, rounded down to the word boundary it is already on where <see cref="StrictAlignmentFlag"/> is on.</summary>
        /// <remarks>
        /// As in <see cref="AlignedSlot"/>, one <c>andi</c> lets the JIT prove the loads of the stack's fields aligned and drop their tests.
        /// It masks the pointer directly: the byref is rarely live after the rounding, so the cast costs no register copy.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe ref EvmStack AlignedStack(ref EvmStack stack)
        {
            Debug.Assert(((nuint)Unsafe.AsPointer(ref stack) & (nuint)(sizeof(nint) - 1)) == 0);
            return ref StrictAlignmentFlag.IsActive
                ? ref Unsafe.AsRef<EvmStack>((void*)((nuint)Unsafe.AsPointer(ref stack) & ~(nuint)(sizeof(nint) - 1)))
                : ref stack;
        }

        /// <summary>The big-endian 16-bit value in the two lowest bytes of <paramref name="bytes"/>; the bytes above them are ignored.</summary>
        /// <remarks>
        /// The full-width byte reversal leaves it zero-extended by its shift, where a 16-bit one needs one more instruction
        /// to clear the bits above it. Without Zbb a reversal is a long software sequence, so the two bytes swap directly.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint ReadBigEndianUInt16(ulong bytes) => ZkEvmBitOperations.HasByteReverse
            ? (nuint)(BinaryPrimitives.ReverseEndianness(bytes) >> 48)
            : (nuint)(((bytes & 0xFF) << 8) | ((bytes >> 8) & 0xFF));

        /// <summary>The big-endian 16-bit value in the two bytes at <paramref name="bytes"/>, which need not be aligned.</summary>
        /// <remarks>Where <see cref="StrictAlignmentFlag"/> is on, or on Airbender, which also rejects misaligned accesses, the bytes are read one at a time, already in big-endian order: half the time they are misaligned, and there the alignment-tested load would assemble them from bytes anyway.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint ReadBigEndianUInt16(ref byte bytes) => StrictAlignmentFlag.IsActive || AirbenderFlag.IsActive
            ? ((nuint)bytes << 8) | Unsafe.Add(ref bytes, 1)
            : ReadBigEndianUInt16(Unsafe.ReadUnaligned<ushort>(ref bytes));

        /// <summary>Marks a direct tail transfer that the opcode weaver emits into the calling handler.</summary>
        /// <remarks>The target is last to match the calli evaluation stack; this method must never survive weaving.</remarks>
        private static EvmExceptionType TailDispatch(
            ref EvmStack stack, nuint gas, ref DispatchState state, ref byte ip, nint head,
            nint* handlers, ref byte code, ref byte sp, nint target) =>
            throw new InvalidOperationException("Guest tail dispatch was not woven.");

        /// <summary>
        /// A comparison, fused with the branch after it - <c>PUSH2</c> <c>JUMPI</c>, <c>ISZERO</c> <c>PUSH2</c> <c>JUMPI</c>, or <c>PUSH1</c> <c>JUMPI</c> -
        /// whenever that branch falls through or lands on a destination the incremental bitmap already holds.
        /// </summary>
        /// <remarks>
        /// Compilers branch on nearly every comparison they emit, so fusing saves the comparison its result slot and
        /// the push its dispatch. The fused step charges every opcode it covers; with too little gas for all of them,
        /// or a branch it cannot take in line, the comparison runs alone and the opcodes after it run as they would. A
        /// taken branch into the code that the bitmap does not hold yet runs the opcodes before the JUMPI and goes to
        /// <see cref="ExecuteJumpToUnanalyzedDestination{TConditional}"/>.
        /// A short stack or gas runs the shared handler of the comparison instead, which faults on it.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteCondition<TCondition>(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
            where TCondition : struct, IStackCondition
        {
            nuint analyzed;
            nint skipped;
            if (head >= TCondition.Inputs)
            {
                // Addressed off the head: keeps the handler frameless.
                ref ulong end = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0));
                ulong condition = TCondition.Evaluate(ref end) ? 1UL : 0UL;
                // PUSH2, its two immediates and JUMPI; the first is the opcode an unfused comparison dispatches on.
                ref byte opcode = ref ip;
                ref byte after = ref Unsafe.Add(ref opcode, 1);
                uint branch = CodeWord(ref after);
                // The branch's PUSH2 overflows a stack the comparison leaves full, which only ISZERO can.
                if (TCondition.Inputs > 1 || head < EvmStack.MaxStackSize - 1)
                {
                    if (CodeByte(branch, ref after, 0) == (byte)Instruction.PUSH2 && CodeByte(branch, ref after, 3) == (byte)Instruction.JUMPI)
                    {
                        // The fused step charges the comparison with every opcode after it in one test.
                        if (condition == 0)
                        {
                            if (TryCharge(ref gas, 2 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost))
                            {
                                head -= TCondition.Inputs;
                                sp = ref Moved(ref sp, -TCondition.Inputs);
                                ip = ref Unsafe.Add(ref ip, 5);
                                goto Dispatch;
                            }
                        }
                        else
                        {
                            ulong jumpGas = JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                            nuint destination = CodeUInt16(branch, ref after, 1);
                            if (destination < (nuint)AlignedStack(ref stack).CodeLength && TryCharge(ref gas, 2 * VeryLowGasCost.GasCost + jumpGas))
                            {
                                if (!AlignedStack(ref stack).IsAnalyzedJumpDestination(destination))
                                {
                                    analyzed = destination;
                                    skipped = 1 + 3;
                                    // The analysis charges the jump itself.
                                    gas += (nuint)(jumpGas);
                                    goto AnalyzeBranch;
                                }

                                head -= TCondition.Inputs;
                                sp = ref Moved(ref sp, -TCondition.Inputs);
                                ip = ref Unsafe.Add(ref code, (nint)destination + 1);
                                goto Dispatch;
                            }
                        }
                    }
                    // An ISZERO between the comparison and the branch only inverts what the branch tests.
                    else if (CodeByte(branch, ref after, 0) == (byte)Instruction.ISZERO)
                    {
                        ref byte afterInversion = ref Unsafe.Add(ref opcode, 2);
                        uint inverted = CodeWord(ref afterInversion);
                        if (CodeByte(inverted, ref afterInversion, 0) == (byte)Instruction.PUSH2 && CodeByte(inverted, ref afterInversion, 3) == (byte)Instruction.JUMPI)
                        {
                            if (condition != 0)
                            {
                                if (TryCharge(ref gas, 3 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost))
                                {
                                    head -= TCondition.Inputs;
                                    sp = ref Moved(ref sp, -TCondition.Inputs);
                                    ip = ref Unsafe.Add(ref ip, 6);
                                    goto Dispatch;
                                }
                            }
                            else
                            {
                                ulong jumpGas = JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                                nuint destination = CodeUInt16(inverted, ref afterInversion, 1);
                                if (destination < (nuint)AlignedStack(ref stack).CodeLength && TryCharge(ref gas, 3 * VeryLowGasCost.GasCost + jumpGas))
                                {
                                    if (!AlignedStack(ref stack).IsAnalyzedJumpDestination(destination))
                                    {
                                        analyzed = destination;
                                        skipped = 1 + 1 + 3;
                                        // The analysis charges the jump itself.
                                        gas += (nuint)(jumpGas);
                                        goto AnalyzeBranch;
                                    }

                                    head -= TCondition.Inputs;
                                    sp = ref Moved(ref sp, -TCondition.Inputs);
                                    ip = ref Unsafe.Add(ref code, (nint)destination + 1);
                                    goto Dispatch;
                                }
                            }
                        }
                    }
                    // Code shorter than 256 bytes pushes its destinations with PUSH1.
                    else if (CodeByte(branch, ref after, 0) == (byte)Instruction.PUSH1 && CodeByte(branch, ref after, 2) == (byte)Instruction.JUMPI)
                    {
                        if (condition == 0)
                        {
                            if (TryCharge(ref gas, 2 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost))
                            {
                                head -= TCondition.Inputs;
                                sp = ref Moved(ref sp, -TCondition.Inputs);
                                ip = ref Unsafe.Add(ref ip, 4);
                                goto Dispatch;
                            }
                        }
                        else
                        {
                            ulong jumpGas = JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                            nuint destination = CodeByte(branch, ref after, 1);
                            if (destination < (nuint)stack.CodeLength && TryCharge(ref gas, 2 * VeryLowGasCost.GasCost + jumpGas))
                            {
                                if (!stack.IsAnalyzedJumpDestination(destination))
                                {
                                    analyzed = destination;
                                    skipped = 1 + 2;
                                    // The analysis charges the jump itself.
                                    gas += (nuint)(jumpGas);
                                    goto AnalyzeBranch;
                                }

                                head -= TCondition.Inputs;
                                sp = ref Moved(ref sp, -TCondition.Inputs);
                                ip = ref Unsafe.Add(ref code, (nint)destination + 1);
                                goto Dispatch;
                            }
                        }
                    }
                }

                // Unfused: the result replaces the deepest input, in limb layout.
                if (TryCharge(ref gas, VeryLowGasCost.GasCost))
                {
                    nint result = -TCondition.Inputs * LimbsPerWord;
                    Unsafe.Add(ref end, result) = condition;
                    Unsafe.Add(ref end, result + 1) = 0;
                    Unsafe.Add(ref end, result + 2) = 0;
                    Unsafe.Add(ref end, result + 3) = 0;
                    head -= TCondition.Inputs - 1;
                    sp = ref Moved(ref sp, -(TCondition.Inputs - 1));
                    ip = ref Unsafe.Add(ref ip, 1);
                    nint unfused = AlignedTable(handlers)[AirbenderFlag.IsActive ? PairAt(ref ip) : (ushort)branch];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, unfused);
                }
            }

            nint shared = TCondition.SharedHandler;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);

        Dispatch:
            nint next = AlignedTable(handlers)[PairAt(ref ip)];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);

        AnalyzeBranch:
            // A taken branch to a destination not analyzed yet: the opcodes before the JUMPI run here, leaving it a condition
            // of 1 and its destination, and the JUMPI goes straight to the analysis.
            ref ulong taken = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, TCondition.Inputs));
            SetWord(ref taken, 1);
            SetWord(ref Unsafe.Add(ref taken, LimbsPerWord), analyzed);
            head += 2 - TCondition.Inputs;
            sp = ref Moved(ref sp, 2 - TCondition.Inputs);
            ip = ref Unsafe.Add(ref ip, skipped);
            nint analyze = Entry(&ExecuteJumpToUnanalyzedDestination<OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, analyze);
        }

        /// <summary>The four code bytes at <paramref name="at"/> as one word, where <see cref="CodeByte"/> takes the bytes from it.</summary>
        /// <remarks>
        /// Airbender reads them one by one instead, and this is zero there: its strict alignment makes an unaligned word
        /// an alignment test and a byte gather, and the bytes then cost shifts on top.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint CodeWord(ref byte at) => AirbenderFlag.IsActive ? 0 : Unsafe.ReadUnaligned<uint>(ref at);

        /// <summary>The code byte <paramref name="index"/> bytes past <paramref name="at"/>, from <paramref name="word"/>, the <see cref="CodeWord"/> there.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte CodeByte(uint word, ref byte at, int index) =>
            AirbenderFlag.IsActive ? Unsafe.Add(ref at, index) : (byte)(word >> (8 * index));

        /// <summary>The big-endian 16-bit immediate <paramref name="index"/> bytes past <paramref name="at"/>, from <paramref name="word"/>, the <see cref="CodeWord"/> there.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint CodeUInt16(uint word, ref byte at, int index) =>
            AirbenderFlag.IsActive ? ReadBigEndianUInt16(ref Unsafe.Add(ref at, index)) : ReadBigEndianUInt16(word >> (8 * index));

        /// <summary>A comparison of the words on top of the stack, as <see cref="ExecuteCondition{TCondition}"/> runs it.</summary>
        internal interface IStackCondition
        {
            /// <summary>How many words the comparison consumes.</summary>
            static abstract int Inputs { get; }

            /// <summary>The shared handler of the comparison, which handles every case that faults.</summary>
            static abstract nint SharedHandler { get; }

            /// <summary>Evaluates the comparison on the words below <paramref name="end"/>, the limb above the top word.</summary>
            /// <remarks>Every limb is addressed off <paramref name="end"/> itself, so each access folds its constant into its own offset.
            /// The inputs may be left changed: every caller pops them or overwrites them with the result, and the shared handler
            /// it falls back to on too little gas faults before it reads them.</remarks>
            static abstract bool Evaluate(ref ulong end);
        }

        /// <summary>ISZERO: whether the top word is zero.</summary>
        internal readonly struct IsZeroCondition : IStackCondition
        {
            public static int Inputs => 1;

            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<Math1Opcode<EvmInstructions.OpIsZero, OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong end) => AirbenderFlag.IsActive
                ? AirbenderArithmetic.IsZero(Unsafe.AsPointer(ref Unsafe.Add(ref end, -4)))
                : (Unsafe.Add(ref end, -4) | Unsafe.Add(ref end, -3) | Unsafe.Add(ref end, -2) | Unsafe.Add(ref end, -1)) == 0;
        }

        /// <summary>EQ: whether the top two words are equal.</summary>
        internal readonly struct EqualCondition : IStackCondition
        {
            public static int Inputs => 2;

            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<BitwiseOpcode<EvmInstructions.OpBitwiseEq, OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong end) => AirbenderFlag.IsActive
                ? AirbenderArithmetic.Equal(Unsafe.AsPointer(ref Unsafe.Add(ref end, -8)), Unsafe.AsPointer(ref Unsafe.Add(ref end, -4)))
                : ((Unsafe.Add(ref end, -4) ^ Unsafe.Add(ref end, -8)) | (Unsafe.Add(ref end, -3) ^ Unsafe.Add(ref end, -7)) |
                    (Unsafe.Add(ref end, -2) ^ Unsafe.Add(ref end, -6)) | (Unsafe.Add(ref end, -1) ^ Unsafe.Add(ref end, -5))) == 0;
        }

        /// <summary>LT: whether the top word is below the one under it, unsigned.</summary>
        internal readonly struct LessThanCondition : IStackCondition
        {
            public static int Inputs => 2;

            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<Math2Opcode<EvmInstructions.OpLt, OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong end) => AirbenderFlag.IsActive
                ? AirbenderArithmetic.IsBelowConsuming(Unsafe.AsPointer(ref Unsafe.Add(ref end, -LimbsPerWord)), Unsafe.AsPointer(ref Unsafe.Add(ref end, -2 * LimbsPerWord)), signed: false)
                : IsBelow(ref end, -LimbsPerWord, -2 * LimbsPerWord);
        }

        /// <summary>GT: whether the top word is above the one under it, unsigned.</summary>
        internal readonly struct GreaterThanCondition : IStackCondition
        {
            public static int Inputs => 2;

            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<Math2Opcode<EvmInstructions.OpGt, OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong end) => AirbenderFlag.IsActive
                ? AirbenderArithmetic.IsBelowConsuming(Unsafe.AsPointer(ref Unsafe.Add(ref end, -2 * LimbsPerWord)), Unsafe.AsPointer(ref Unsafe.Add(ref end, -LimbsPerWord)), signed: false)
                : IsBelow(ref end, -2 * LimbsPerWord, -LimbsPerWord);
        }

        /// <summary>SLT: whether the top word is below the one under it, both as two's complement.</summary>
        internal readonly struct SignedLessThanCondition : IStackCondition
        {
            public static int Inputs => 2;

            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<Math2Opcode<EvmInstructions.OpSLt, OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong end) => AirbenderFlag.IsActive
                ? AirbenderArithmetic.IsBelowConsuming(Unsafe.AsPointer(ref Unsafe.Add(ref end, -LimbsPerWord)), Unsafe.AsPointer(ref Unsafe.Add(ref end, -2 * LimbsPerWord)), signed: true)
                : IsSignedBelow(ref end, -LimbsPerWord, -2 * LimbsPerWord);
        }

        /// <summary>SGT: whether the top word is above the one under it, both as two's complement.</summary>
        internal readonly struct SignedGreaterThanCondition : IStackCondition
        {
            public static int Inputs => 2;

            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<Math2Opcode<EvmInstructions.OpSGt, OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong end) => AirbenderFlag.IsActive
                ? AirbenderArithmetic.IsBelowConsuming(Unsafe.AsPointer(ref Unsafe.Add(ref end, -2 * LimbsPerWord)), Unsafe.AsPointer(ref Unsafe.Add(ref end, -LimbsPerWord)), signed: true)
                : IsSignedBelow(ref end, -2 * LimbsPerWord, -LimbsPerWord);
        }

        /// <summary>A fixed-cost operation on the top two words, with both addressed off the head.</summary>
        /// <remarks>A short stack or gas runs the shared handler of the operation instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteBinary<TOperation>(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
            where TOperation : struct, IStackBinaryOperation
        {
            if (head > 1 && ChargeUntested(ref gas, TOperation.GasCost))
            {
                TOperation.Apply(ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0)));
                head--;
                sp = ref Moved(ref sp, -1);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = TOperation.SharedHandler;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>An operation on the top two words, as <see cref="ExecuteBinary{TOperation}"/> runs it.</summary>
        internal interface IStackBinaryOperation
        {
            /// <summary>The operation's fixed gas cost.</summary>
            static virtual ulong GasCost => VeryLowGasCost.GasCost;

            /// <summary>The shared handler of the operation, which handles every case that faults.</summary>
            static abstract nint SharedHandler { get; }

            /// <summary>Replaces the second word with the result.</summary>
            /// <param name="end">The limb above the top word: the top word's limbs are at -4 to -1 from it, the second's at -8 to -5.</param>
            /// <remarks>
            /// Every limb is addressed off <paramref name="end"/> itself, so each access folds its constant into its own
            /// offset; a compound assignment would take the address into a register first.
            /// </remarks>
            static abstract void Apply(ref ulong end);
        }

        /// <summary>ADD: the top word plus the second, modulo 2^256.</summary>
        internal readonly struct AddOperation : IStackBinaryOperation
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<Math2Opcode<EvmInstructions.OpAdd, OffFlag>, OffFlag, OffFlag, OnFlag>);

            /// <remarks>
            /// Nearly every addition adds a word below 2^64 - an offset, a length, a count - whose upper limbs add nothing,
            /// so only a carry out of the low limb, which is rare, reaches the limbs above it.
            /// </remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                if (AirbenderFlag.IsActive)
                {
                    AirbenderArithmetic.Add(Unsafe.AsPointer(ref Unsafe.Add(ref end, -8)), Unsafe.AsPointer(ref Unsafe.Add(ref end, -4)));
                    return;
                }

                ulong left = Unsafe.Add(ref end, -8);
                ulong sum = left + Unsafe.Add(ref end, -4);
                Unsafe.Add(ref end, -8) = sum;
                if ((Unsafe.Add(ref end, -3) | Unsafe.Add(ref end, -2) | Unsafe.Add(ref end, -1)) == 0)
                {
                    if (sum < left) IncrementUpperLimbs(ref end);
                    return;
                }

                ulong carry = sum < left ? 1UL : 0UL;
                carry = AddLimb(ref end, -7, carry);
                carry = AddLimb(ref end, -6, carry);
                Unsafe.Add(ref end, -5) = Unsafe.Add(ref end, -5) + Unsafe.Add(ref end, -1) + carry;
            }

            /// <summary>Adds a carry into the second word's limbs above its low one.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void IncrementUpperLimbs(ref ulong end)
            {
                ulong limb = Unsafe.Add(ref end, -7) + 1;
                Unsafe.Add(ref end, -7) = limb;
                if (limb != 0) return;
                limb = Unsafe.Add(ref end, -6) + 1;
                Unsafe.Add(ref end, -6) = limb;
                if (limb != 0) return;
                Unsafe.Add(ref end, -5) = Unsafe.Add(ref end, -5) + 1;
            }

            /// <summary>Adds the top word's limb and <paramref name="carry"/> into the second word's limb at <paramref name="limb"/>, and returns the carry out.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static ulong AddLimb(ref ulong end, nint limb, ulong carry)
            {
                ulong left = Unsafe.Add(ref end, limb);
                ulong sum = left + Unsafe.Add(ref end, limb + 4);
                ulong carryOut = sum < left ? 1UL : 0UL;
                sum += carry;
                carryOut += sum < carry ? 1UL : 0UL;
                Unsafe.Add(ref end, limb) = sum;
                return carryOut;
            }
        }

        /// <summary>SUB: the top word minus the second, modulo 2^256.</summary>
        internal readonly struct SubtractOperation : IStackBinaryOperation
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<Math2Opcode<EvmInstructions.OpSub, OffFlag>, OffFlag, OffFlag, OnFlag>);

            /// <remarks>
            /// Most subtractions take a word below 2^64 away, whose upper limbs take nothing: the minuend's carry over,
            /// less a borrow out of the low limb, which only rarely reaches past the next one.
            /// </remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                if (AirbenderFlag.IsActive)
                {
                    AirbenderArithmetic.SubtractFrom(Unsafe.AsPointer(ref Unsafe.Add(ref end, -8)), Unsafe.AsPointer(ref Unsafe.Add(ref end, -4)));
                    return;
                }

                ulong minuend = Unsafe.Add(ref end, -4);
                ulong subtrahend = Unsafe.Add(ref end, -8);
                ulong borrow = minuend < subtrahend ? 1UL : 0UL;
                Unsafe.Add(ref end, -8) = minuend - subtrahend;
                if ((Unsafe.Add(ref end, -7) | Unsafe.Add(ref end, -6) | Unsafe.Add(ref end, -5)) == 0)
                {
                    ulong upper = Unsafe.Add(ref end, -3);
                    Unsafe.Add(ref end, -7) = upper - borrow;
                    if (upper >= borrow)
                    {
                        Unsafe.Add(ref end, -6) = Unsafe.Add(ref end, -2);
                        Unsafe.Add(ref end, -5) = Unsafe.Add(ref end, -1);
                        return;
                    }

                    // The borrow went through a zero limb, which leaves it for the limbs above.
                    upper = Unsafe.Add(ref end, -2);
                    Unsafe.Add(ref end, -6) = upper - 1;
                    Unsafe.Add(ref end, -5) = Unsafe.Add(ref end, -1) - (upper == 0 ? 1UL : 0UL);
                    return;
                }

                borrow = SubtractLimb(ref end, -7, borrow);
                borrow = SubtractLimb(ref end, -6, borrow);
                Unsafe.Add(ref end, -5) = Unsafe.Add(ref end, -1) - Unsafe.Add(ref end, -5) - borrow;
            }

            /// <summary>Writes the top word's limb less the second word's limb at <paramref name="limb"/> and <paramref name="borrow"/> over the latter, and returns the borrow out.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static ulong SubtractLimb(ref ulong end, nint limb, ulong borrow)
            {
                ulong minuend = Unsafe.Add(ref end, limb + 4);
                ulong subtrahend = Unsafe.Add(ref end, limb);
                ulong difference = minuend - subtrahend;
                ulong borrowOut = minuend < subtrahend ? 1UL : 0UL;
                borrowOut += difference < borrow ? 1UL : 0UL;
                Unsafe.Add(ref end, limb) = difference - borrow;
                return borrowOut;
            }
        }

        /// <summary>AND: the bitwise and of the top two words.</summary>
        internal readonly struct AndOperation : IStackBinaryOperation
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<BitwiseOpcode<EvmInstructions.OpBitwiseAnd, OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                Unsafe.Add(ref end, -8) = Unsafe.Add(ref end, -8) & Unsafe.Add(ref end, -4);
                Unsafe.Add(ref end, -7) = Unsafe.Add(ref end, -7) & Unsafe.Add(ref end, -3);
                Unsafe.Add(ref end, -6) = Unsafe.Add(ref end, -6) & Unsafe.Add(ref end, -2);
                Unsafe.Add(ref end, -5) = Unsafe.Add(ref end, -5) & Unsafe.Add(ref end, -1);
            }
        }

        /// <summary>OR: the bitwise or of the top two words.</summary>
        internal readonly struct OrOperation : IStackBinaryOperation
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<BitwiseOpcode<EvmInstructions.OpBitwiseOr, OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                Unsafe.Add(ref end, -8) = Unsafe.Add(ref end, -8) | Unsafe.Add(ref end, -4);
                Unsafe.Add(ref end, -7) = Unsafe.Add(ref end, -7) | Unsafe.Add(ref end, -3);
                Unsafe.Add(ref end, -6) = Unsafe.Add(ref end, -6) | Unsafe.Add(ref end, -2);
                Unsafe.Add(ref end, -5) = Unsafe.Add(ref end, -5) | Unsafe.Add(ref end, -1);
            }
        }

        /// <summary>XOR: the bitwise exclusive or of the top two words.</summary>
        internal readonly struct XorOperation : IStackBinaryOperation
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<BitwiseOpcode<EvmInstructions.OpBitwiseXor, OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                Unsafe.Add(ref end, -8) = Unsafe.Add(ref end, -8) ^ Unsafe.Add(ref end, -4);
                Unsafe.Add(ref end, -7) = Unsafe.Add(ref end, -7) ^ Unsafe.Add(ref end, -3);
                Unsafe.Add(ref end, -6) = Unsafe.Add(ref end, -6) ^ Unsafe.Add(ref end, -2);
                Unsafe.Add(ref end, -5) = Unsafe.Add(ref end, -5) ^ Unsafe.Add(ref end, -1);
            }
        }

        /// <summary>SAR: the second word shifted right by the top word, filling with its sign; a shift of 256 or more leaves only the sign.</summary>
        internal readonly struct ArithmeticShiftRightOperation : IStackBinaryOperation
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<SarOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                ulong bits = Unsafe.Add(ref end, -4);
                if ((Unsafe.Add(ref end, -3) | Unsafe.Add(ref end, -2) | Unsafe.Add(ref end, -1) | (bits >> 8)) == 0)
                {
                    if (AirbenderFlag.IsActive)
                        AirbenderArithmetic.ShiftRightArithmetic(Unsafe.AsPointer(ref Unsafe.Add(ref end, -8)), (int)bits);
                    else
                        ShiftRight(ref Unsafe.Add(ref end, -8), (int)bits, arithmetic: true);
                    return;
                }

                ulong fill = (ulong)((long)Unsafe.Add(ref end, -5) >> 63);
                Unsafe.Add(ref end, -8) = fill;
                Unsafe.Add(ref end, -7) = fill;
                Unsafe.Add(ref end, -6) = fill;
                Unsafe.Add(ref end, -5) = fill;
            }
        }

        /// <summary>BYTE: the byte of the second word the top word indexes, counted from its most significant; zero past the word.</summary>
        internal readonly struct ByteOperation : IStackBinaryOperation
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<ByteOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);

            /// <remarks>The byte is shifted out of its limb, where loading it alone would be a narrow access.</remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                ulong index = Unsafe.Add(ref end, -4);
                ulong selected = 0;
                if ((Unsafe.Add(ref end, -3) | Unsafe.Add(ref end, -2) | Unsafe.Add(ref end, -1)) == 0 && index < EvmStack.WordSize)
                {
                    // Big-endian byte `index` is byte `31 - index` of the word's limb layout.
                    int fromLow = EvmStack.WordSize - 1 - (int)index;
                    selected = (byte)(Unsafe.Add(ref end, -8 + (fromLow >> 3)) >> ((fromLow & 7) * 8));
                }

                SetWord(ref Unsafe.Add(ref end, -8), selected);
            }
        }

        /// <summary>SIGNEXTEND: the second word extended from the sign of its byte the top word indexes, counted from its least significant.</summary>
        internal readonly struct SignExtendOperation : IStackBinaryOperation
        {
            public static ulong GasCost => LowGasCost.GasCost;

            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<SignExtendOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);

            /// <remarks>
            /// An index of 31 or more leaves the word as it is. The limb holding the sign byte is extended in place by an
            /// arithmetic shift down and up, and the limbs above it take the fill.
            /// </remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                ulong position = Unsafe.Add(ref end, -4);
                if ((Unsafe.Add(ref end, -3) | Unsafe.Add(ref end, -2) | Unsafe.Add(ref end, -1)) != 0 || position >= EvmStack.WordSize - 1)
                    return;

                nint limb = (nint)(position >> 3);
                int unused = 56 - (int)(position & 7) * 8;
                ref ulong partial = ref Unsafe.Add(ref end, -8 + limb);
                long extended = ((long)partial << unused) >> unused;
                partial = (ulong)extended;
                ulong fill = (ulong)(extended >> 63);
                if (limb < 1) Unsafe.Add(ref end, -7) = fill;
                if (limb < 2) Unsafe.Add(ref end, -6) = fill;
                if (limb < 3) Unsafe.Add(ref end, -5) = fill;
            }
        }

        /// <summary>NOT of the top word, in place.</summary>
        /// <remarks>A short stack or gas runs the shared NOT handler instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteNot(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 0 && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                NotStep.Apply(ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0)), ref ip);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<Math1Opcode<EvmInstructions.OpNot, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>ADDMOD or MULMOD, as <typeparamref name="TOperation"/> computes it, with its operands and result in their stack slots.</summary>
        /// <remarks>
        /// The shared handler copies the three operands out and the result back. A zero modulus leaves the zero its
        /// slot holds. A short stack or gas runs the shared handler instead, which faults on it.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteModular<TOperation>(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
            where TOperation : struct, EvmInstructions.IOpMath3Param
        {
            if (head > 2 && ChargeUntested(ref gas, MidGasCost.GasCost))
            {
                // The result replaces the modulus, the deepest of the three.
                ref ulong modulus = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 3));
                if ((modulus | Unsafe.Add(ref modulus, 1) | Unsafe.Add(ref modulus, 2) | Unsafe.Add(ref modulus, 3)) != 0)
                {
                    if (ZiskFlag.IsActive)
                    {
                        // The stack is pinned. The result must not alias an operand.
                        ulong* m = (ulong*)Unsafe.AsPointer(ref modulus);
                        UInt256 result;
                        if (typeof(TOperation) == typeof(EvmInstructions.OpAddMod))
                            ZiskArithmetic.AddMod(m + 2 * LimbsPerWord, m + LimbsPerWord, m, (ulong*)&result);
                        else
                            ZiskArithmetic.MulMod(m + 2 * LimbsPerWord, m + LimbsPerWord, m, (ulong*)&result);
                        *(UInt256*)m = result;
                    }
                    else
                    {
                        ref UInt256 m = ref Unsafe.As<ulong, UInt256>(ref modulus);
                        TOperation.Operation(in Unsafe.Add(ref m, 2), in Unsafe.Add(ref m, 1), in m, out UInt256 result);
                        m = result;
                    }
                }

                head -= 2;
                if (ZiskFlag.IsActive)
                {
                    // The precompile is a CSR write, not a call, so the carried state is still in its registers.
                    sp = ref Moved(ref sp, -2);
                }
                else
                {
                    // Reloaded rather than held across the call, where each would take a callee-saved register.
                    handlers = state.OpcodeHandlers;
                    code = ref stack.Code;
                    sp = ref SlotAt(ref stack.Bottom, head);
                }

                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<Math3Opcode<TOperation, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>
        /// DUP1, fused with a selector dispatch after it - <c>PUSH4</c> <c>EQ</c> <c>PUSH2</c> <c>JUMPI</c> - whenever that
        /// branch falls through or lands on a destination the incremental bitmap already holds.
        /// </summary>
        /// <remarks>
        /// A contract's dispatcher compares the selector against each function's in turn, so this sequence runs once per
        /// function it passes; fused, it compares the top word with the selector in place and leaves the stack as it
        /// found it. The fused step charges every opcode it covers; with too little gas for all of them, too little room
        /// for the two pushes, or a branch it cannot take in line, DUP1 runs alone. A match whose destination the bitmap
        /// does not hold yet runs the opcodes before the JUMPI and goes to
        /// <see cref="ExecuteJumpToUnanalyzedDestination{TConditional}"/>. A short stack or gas, or a full one, runs the
        /// shared DUP1 handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteDup1(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            // The selector comparison lives in its own handler, which needs a frame: a plain DUP1 keeps none.
            if (Unsafe.Add(ref ip, 1) == (byte)Instruction.PUSH4 && Unsafe.Add(ref ip, 6) == (byte)Instruction.EQ)
            {
                nint selector = Entry(&ExecuteDup1Selector);
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, selector);
            }

            if ((nuint)(head - 1) < (nuint)(EvmStack.MaxStackSize - 2) && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                DupStep<EvmInstructions.Op1>.Apply(ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0)), ref ip);
                head++;
                sp = ref Moved(ref sp, 1);
                ip = ref Unsafe.Add(ref ip, 1);
                nint unfused = AlignedTable(handlers)[ip];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, unfused);
            }

            nint shared = Entry(&ExecuteOpcode<DupOpcode<EvmInstructions.Op1, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary><see cref="ExecuteDup1"/> followed by PUSH4 and EQ: the selector comparison, fused when PUSH2 and JUMPI follow.</summary>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteDup1Selector(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            // One unsigned test bounds the depth on both sides: on an empty stack the difference wraps past the limit.
            if ((nuint)(head - 1) < (nuint)(EvmStack.MaxStackSize - 2))
            {
                ref ulong top = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                // PUSH4, its four immediates, EQ, PUSH2, its two immediates and JUMPI.
                ref byte opcode = ref ip;
                byte following = Unsafe.Add(ref opcode, 1);
                if (following == (byte)Instruction.PUSH4 &&
                    Unsafe.Add(ref opcode, 6) == (byte)Instruction.EQ && head < EvmStack.MaxStackSize - 2)
                {
                    ref byte after = ref Unsafe.Add(ref opcode, 7);
                    uint branch = CodeWord(ref after);
                    if (CodeByte(branch, ref after, 0) == (byte)Instruction.PUSH2 && CodeByte(branch, ref after, 3) == (byte)Instruction.JUMPI)
                    {
                        // The selector's word, reversed, holds it zero-extended above bit 32, where a 32-bit read needs extending.
                        // A 32-bit guest has no byte reversal and reads the four bytes in order.
                        ulong selector = IntPtr.Size == sizeof(uint)
                            ? ((uint)Unsafe.Add(ref opcode, 2) << 24) | ((uint)Unsafe.Add(ref opcode, 3) << 16) |
                                ((uint)Unsafe.Add(ref opcode, 4) << 8) | Unsafe.Add(ref opcode, 5)
                            : BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref opcode, 2))) >> 32;
                        // The fused step charges DUP1 with every opcode after it in one test.
                        if (((top ^ selector) | Unsafe.Add(ref top, 1) | Unsafe.Add(ref top, 2) | Unsafe.Add(ref top, 3)) != 0)
                        {
                            if (TryCharge(ref gas, 4 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost))
                            {
                                ip = ref Unsafe.Add(ref ip, 11);
                                goto Dispatch;
                            }
                        }
                        else
                        {
                            ulong jumpGas = JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                            nuint destination = CodeUInt16(branch, ref after, 1);
                            if (destination < (nuint)AlignedStack(ref stack).CodeLength && TryCharge(ref gas, 4 * VeryLowGasCost.GasCost + jumpGas))
                            {
                                if (!AlignedStack(ref stack).IsAnalyzedJumpDestination(destination))
                                {
                                    // Straight to the analysis: the opcodes before the JUMPI leave it a condition of 1 and its destination.
                                    ref ulong taken = ref Unsafe.Add(ref top, LimbsPerWord);
                                    SetWord(ref taken, 1);
                                    SetWord(ref Unsafe.Add(ref taken, LimbsPerWord), destination);
                                    head += 2;
                                    sp = ref Moved(ref sp, 2);
                                    // The analysis charges the jump itself.
                                    gas += (nuint)(jumpGas);
                                    ip = ref Unsafe.Add(ref ip, 10);
                                    nint analyze = Entry(&ExecuteJumpToUnanalyzedDestination<OnFlag>);
                                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, analyze);
                                }

                                ip = ref Unsafe.Add(ref code, (nint)destination + 1);
                                goto Dispatch;
                            }
                        }
                    }
                }

                if (ChargeUntested(ref gas, VeryLowGasCost.GasCost))
                {
                    ref ulong copy = ref Unsafe.Add(ref top, EvmStack.WordSize / sizeof(ulong));
                    copy = top;
                    Unsafe.Add(ref copy, 1) = Unsafe.Add(ref top, 1);
                    Unsafe.Add(ref copy, 2) = Unsafe.Add(ref top, 2);
                    Unsafe.Add(ref copy, 3) = Unsafe.Add(ref top, 3);
                    head++;
                    sp = ref Moved(ref sp, 1);
                    ip = ref Unsafe.Add(ref ip, 1);
                    nint unfused = AlignedTable(handlers)[following];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, unfused);
                }
            }

            nint shared = Entry(&ExecuteOpcode<DupOpcode<EvmInstructions.Op1, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);

        Dispatch:
            nint next = AlignedTable(handlers)[PairAt(ref ip)];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
        }

        /// <summary>DUPn, for every n but 1, with both words addressed off the head.</summary>
        /// <remarks>A short stack or gas, or a full one, runs the shared DUPn handler instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteDup<TOpCount>(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
            where TOpCount : struct, EvmInstructions.IOpCount
        {
            // One unsigned test bounds the depth on both sides: below the source the difference wraps past the limit.
            if ((nuint)(head - TOpCount.Count) < (nuint)(EvmStack.MaxStackSize - 1 - TOpCount.Count) && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                DupStep<TOpCount>.Apply(ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0)), ref ip);
                head++;
                sp = ref Moved(ref sp, 1);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<DupOpcode<TOpCount, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>POP, fused with a POP after it.</summary>
        /// <remarks>
        /// Compilers drop the words a scope leaves in runs, so the fused step saves the second POP its dispatch; one
        /// short of stack or gas runs alone after the first. A short stack or gas runs the shared POP handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePop(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 0 && ChargeUntested(ref gas, BaseGasCost.GasCost))
            {
                head--;
                sp = ref Moved(ref sp, -1);
                byte following = Unsafe.Add(ref ip, 1);
                if (following == (byte)Instruction.POP && head > 0)
                {
                    head--;
                    sp = ref Moved(ref sp, -1);
                    gas -= (nuint)(BaseGasCost.GasCost);
                    ip = ref Unsafe.Add(ref ip, 2);
                    nint fused = AlignedTable(handlers)[PairAt(ref ip)];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, fused);
                }

                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[following];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<PopOpcode, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>SWAPn with both words addressed off the head, one limb pair at a time.</summary>
        /// <remarks>
        /// Both words lie a constant distance below the head, so every limb access takes its offset from one address,
        /// and a pair at a time the swap needs two registers. A short stack or gas runs the shared SWAPn handler
        /// instead, which faults on it.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteSwap<TOpCount>(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
            where TOpCount : struct, EvmInstructions.IOpCount
        {
            if (head > TOpCount.Count && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                SwapStep<TOpCount>.Apply(ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0)), ref ip);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<SwapOpcode<TOpCount, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>Exchanges the limbs at <paramref name="top"/> and <paramref name="bottom"/> limbs from <paramref name="end"/>.</summary>
        /// <remarks>Both are addressed off <paramref name="end"/> itself, so each access folds its constant into its own offset.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SwapLimbs(ref ulong end, nint top, nint bottom)
        {
            ulong limb = Unsafe.Add(ref end, bottom);
            Unsafe.Add(ref end, bottom) = Unsafe.Add(ref end, top);
            Unsafe.Add(ref end, top) = limb;
        }

        /// <summary>PUSH1 with its immediate and the opcode after it read from one address.</summary>
        /// <remarks>
        /// A short gas or a full stack runs the shared PUSH1 handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePush1(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head < EvmStack.MaxStackSize - 1 && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                // Both read off the opcode's address, so the immediate's needs no add of its own.
                ref byte opcode = ref ip;
                nint next = AlignedTable(handlers)[PairAt(ref Unsafe.Add(ref opcode, 2))];
                SetWord(ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0)), Unsafe.Add(ref opcode, 1));
                ip = ref Unsafe.Add(ref ip, 2);
                head++;
                sp = ref Moved(ref sp, 1);
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<PushOpcode<EvmInstructions.Op1, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>JUMPDEST, which only charges its gas.</summary>
        /// <remarks>A short gas runs the shared JUMPDEST handler instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteJumpDest(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (TryCharge(ref gas, JumpDestGasCost.GasCost))
            {
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<JumpDestOpcode, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>An opcode that pushes a value below 2^64 it reads without a call, as <typeparamref name="TValue"/> reads it.</summary>
        /// <remarks>A short gas or a full stack runs the shared handler of the opcode instead.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePushValue<TValue>(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
            where TValue : struct, IStackValue
        {
            if (head < EvmStack.MaxStackSize - 1 && TryCharge(ref gas, BaseGasCost.GasCost))
            {
                SetWord(ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0)), TValue.Read(ref stack, ref state, gas));
                head++;
                sp = ref Moved(ref sp, 1);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = TValue.SharedHandler;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>A value of base cost that <see cref="ExecutePushValue{TValue}"/> pushes.</summary>
        internal interface IStackValue
        {
            /// <summary>The shared handler of the opcode, which handles every case that faults.</summary>
            static abstract nint SharedHandler { get; }

            /// <summary>Reads the value out of <paramref name="stack"/> or <paramref name="state"/>.</summary>
            /// <param name="stack">The running frame's stack.</param>
            /// <param name="state">The chain's state.</param>
            /// <param name="gas">The remaining gas, the opcode's charge paid.</param>
            static abstract ulong Read(ref EvmStack stack, ref DispatchState state, ulong gas);
        }

        /// <summary>PUSH0: zero.</summary>
        internal readonly struct ZeroValue : IStackValue
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<Push0Opcode<OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ulong Read(ref EvmStack stack, ref DispatchState state, ulong gas) => 0;
        }

        /// <summary>CALLDATASIZE: the length of the input data the stack holds.</summary>
        internal readonly struct CallDataSizeValue : IStackValue
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<EnvUInt32Opcode<EvmInstructions.OpCallDataSize<TGasPolicy>, OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ulong Read(ref EvmStack stack, ref DispatchState state, ulong gas) => (ulong)stack.InputDataLength;
        }

        /// <summary>GAS: the gas left once its own charge is paid.</summary>
        internal readonly struct GasValue : IStackValue
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<GasOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ulong Read(ref EvmStack stack, ref DispatchState state, ulong gas) => gas;
        }

        /// <summary>RETURNDATASIZE: the length of the last call's return data.</summary>
        internal readonly struct ReturnDataSizeValue : IStackValue
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<ReturnDataSizeOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ulong Read(ref EvmStack stack, ref DispatchState state, ulong gas) => (uint)state.Vm.ReturnDataBuffer.Length;
        }

        /// <summary>PUSHn for n from 3 to 32, with the immediates read a limb at a time off the instruction's address.</summary>
        /// <remarks>
        /// Immediates that run past the end of the code read the padding's zeros, as the EVM pads them. A short gas or
        /// a full stack runs the shared PUSHn handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePush<TOpCount>(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
            where TOpCount : struct, EvmInstructions.IOpCount
        {
            if (head < EvmStack.MaxStackSize - 1 && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong slot = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0));
                ref byte immediates = ref Unsafe.Add(ref ip, 1);
                // The leading limb takes the bytes that do not fill a whole one, or a whole one; the rest follow it whole.
                int leadingBytes = ((TOpCount.Count - 1) & 7) + 1;
                int limbs = (TOpCount.Count + 7) >> 3;
                ulong leading = ZkEvmBitOperations.ReadUInt64BigEndian(ref immediates) >> (64 - 8 * leadingBytes);
                ref byte rest = ref Unsafe.Add(ref immediates, leadingBytes);
                if (limbs == 1)
                {
                    SetWord(ref slot, leading);
                }
                else
                {
                    Unsafe.Add(ref slot, limbs - 1) = leading;
                    Unsafe.Add(ref slot, limbs - 2) = ReadBigEndianLimb(ref rest, 0);
                    if (limbs > 2) Unsafe.Add(ref slot, limbs - 3) = ReadBigEndianLimb(ref rest, 1);
                    if (limbs > 3) slot = ReadBigEndianLimb(ref rest, 2);
                    if (limbs < 4) Unsafe.Add(ref slot, 3) = 0;
                    if (limbs < 3) Unsafe.Add(ref slot, 2) = 0;
                }

                head++;
                sp = ref Moved(ref sp, 1);
                ip = ref Unsafe.Add(ref ip, 1 + TOpCount.Count);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<PushOpcode<TOpCount, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>The big-endian limb <paramref name="index"/> limbs past <paramref name="bytes"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ReadBigEndianLimb(ref byte bytes, nint index) =>
            ZkEvmBitOperations.ReadUInt64BigEndian(ref Unsafe.Add(ref bytes, index * sizeof(ulong)));

        /// <summary>PUSH2, fused with a JUMP or JUMPI after it whenever <see cref="EvmInstructions.InstructionPush2{TGasPolicy, TTracingInst}"/> would fuse them.</summary>
        /// <remarks>
        /// Fuses on the same conditions: a destination the incremental bitmap already holds, or a zero JUMPI
        /// condition that never reads it. A taken jump into the code that the bitmap does not hold yet pushes and goes
        /// to <see cref="ExecuteJumpToUnanalyzedDestination{TConditional}"/>. Every case that could fault - short gas, a
        /// full stack or a fused jump's condition missing - runs the shared PUSH2 handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePush2(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            ulong pushGas = VeryLowGasCost.GasCost;
            if (head >= EvmStack.MaxStackSize - 1)
                goto Shared;

            // A fused step charges the push with the rest in one test, and short of all of it runs the shared handler.
            ref byte opcode = ref ip;
            nuint value = ReadBigEndianUInt16(ref Unsafe.Add(ref opcode, 1));
            byte following = Unsafe.Add(ref opcode, 3);
            nint analyze;
            if (following == (byte)Instruction.JUMP)
            {
                if (value < (nuint)AlignedStack(ref stack).CodeLength)
                {
                    ulong jumpGas = JumpGasCost.GasCost + JumpDestGasCost.GasCost;
                    if (!TryCharge(ref gas, pushGas + jumpGas))
                        goto Shared;

                    if (!AlignedStack(ref stack).IsAnalyzedJumpDestination(value))
                    {
                        // The analysis charges the jump itself.
                        gas += (nuint)(jumpGas);
                        analyze = Entry(&ExecuteJumpToUnanalyzedDestination<OffFlag>);
                        goto Analyze;
                    }

                    ip = ref Unsafe.Add(ref code, (nint)value + 1);
                    goto Dispatch;
                }
            }
            else if (following == (byte)Instruction.JUMPI)
            {
                // The JUMPI would find no condition; the shared handler faults on it as the unfused pair does.
                if (head == 0)
                    goto Shared;

                ref ulong condition = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                if (IsZeroWord(ref condition))
                {
                    if (!TryCharge(ref gas, pushGas + JumpIGasCost.GasCost))
                        goto Shared;

                    head--;
                    sp = ref Moved(ref sp, -1);
                    ip = ref Unsafe.Add(ref ip, 4);
                    goto Dispatch;
                }

                if (value < (nuint)AlignedStack(ref stack).CodeLength)
                {
                    ulong jumpGas = JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                    if (!TryCharge(ref gas, pushGas + jumpGas))
                        goto Shared;

                    if (!AlignedStack(ref stack).IsAnalyzedJumpDestination(value))
                    {
                        // The analysis charges the jump itself.
                        gas += (nuint)(jumpGas);
                        analyze = Entry(&ExecuteJumpToUnanalyzedDestination<OnFlag>);
                        goto Analyze;
                    }

                    head--;
                    sp = ref Moved(ref sp, -1);
                    ip = ref Unsafe.Add(ref code, (nint)value + 1);
                    goto Dispatch;
                }
            }

            if (!ChargeUntested(ref gas, pushGas))
                goto Shared;

            SetWord(ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0)), value);
            ip = ref Unsafe.Add(ref ip, 3);
            head++;
            sp = ref Moved(ref sp, 1);
            nint next = AlignedTable(handlers)[following];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);

        Dispatch:
            nint target = AlignedTable(handlers)[PairAt(ref ip)];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, target);

        Analyze:
            // Straight to the analysis, which the jump handler would only reach after repeating the tests passed here.
            SetWord(ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0)), value);
            ip = ref Unsafe.Add(ref ip, 3);
            head++;
            sp = ref Moved(ref sp, 1);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, analyze);

        Shared:
            nint shared = Entry(&ExecuteOpcode<Push2Opcode<OffFlag, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>MSTORE of a word that needs no new backing, growing the active memory over it when it has to.</summary>
        /// <remarks>
        /// A word inside the active, initialized memory is stored here, frameless; growing the memory, which calls out,
        /// runs <see cref="ExecuteMStoreGrowing"/>.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteMStoreInsideBacking(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 1 && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong offset = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                if ((Unsafe.Add(ref offset, 1) | Unsafe.Add(ref offset, 2) | Unsafe.Add(ref offset, 3) | (offset >> 32)) == 0)
                {
                    ref byte destination = ref state.Memory.GetActiveInitializedRange(offset, EvmStack.WordSize);
                    if (!Unsafe.IsNullRef(ref destination))
                    {
                        StoreBigEndian(ref destination, ref Unsafe.Add(ref offset, -4));
                        head -= 2;
                        sp = ref Moved(ref sp, -2);
                        ip = ref Unsafe.Add(ref ip, 1);
                        nint next = AlignedTable(handlers)[PairAt(ref ip)];
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
                    }
                }

                gas += (nuint)(VeryLowGasCost.GasCost);
            }

            nint growing = Entry(&ExecuteMStoreGrowing);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, growing);
        }

        /// <summary><see cref="ExecuteMStoreInsideBacking"/> where the word is outside the active or initialized memory.</summary>
        /// <remarks>
        /// Every other case - short gas or stack, a word that outgrows the backing, an offset of 2^32 or more, which runs
        /// out of gas - runs the shared MSTORE handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteMStoreGrowing(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 1 && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong offset = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                if ((Unsafe.Add(ref offset, 1) | Unsafe.Add(ref offset, 2) | Unsafe.Add(ref offset, 3) | (offset >> 32)) == 0)
                {
                    // Charged in the carried gas: a separate copy would take a callee-saved register.
                    ref byte destination = ref state.Memory.TryPrepareWordOverwrite(offset, ref gas);
                    if (!Unsafe.IsNullRef(ref destination))
                    {
                        // The value is the word below the offset; memory holds it big-endian.
                        StoreBigEndian(ref destination, ref Unsafe.Add(ref offset, -4));
                        head -= 2;
                        sp = ref Moved(ref sp, -2);
                        ip = ref Unsafe.Add(ref ip, 1);
                        nint next = AlignedTable(handlers)[PairAt(ref ip)];
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
                    }
                }

                gas += (nuint)(VeryLowGasCost.GasCost);
            }

            nint shared = Entry(&ExecuteOpcode<MStoreOpcode<OffFlag, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>SLOAD under EIP-2929 without EIP-8038, with the key read from and the value written to its stack slot.</summary>
        /// <remarks>
        /// The shared handler builds the storage cell twice and saves every callee-saved register around it. Running out
        /// of gas leaves the chain here, since the cell is warm by then and the shared handler would charge it as warm.
        /// An empty stack runs the shared SLOAD handler instead, which faults on it, and so does an EIP-8360 TCREATE
        /// context, where SLOAD reads transient storage.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteSLoad<Eip8360>(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
            where Eip8360 : struct, IFlag
        {
            if (head > 0 && !(Eip8360.IsActive && state.Vm.VmState.IsTransientCreateContext))
            {
                VirtualMachine<TGasPolicy> vm = state.Vm;
                vm.MetricsCounters.IncrementSLoad();
                VmState<TGasPolicy> frame = vm.VmState;
                ref UInt256 slot = ref Unsafe.As<byte, UInt256>(ref SlotBelow(ref sp, 1));
                StorageCell storageCell = new(frame.Env.ExecutingAccount, in slot);
                // Warming before the charge is unobservable: running out of gas halts the frame, whose restore drops the cell again.
                ulong cost = frame.AccessTracker.WarmUp(in storageCell) ? GasCostOf.ColdSLoad : GasCostOf.WarmStateRead;
                if (!Covers(gas, cost))
                    return ExitChain(ref state, 0, (nint)Unsafe.ByteOffset(ref code, ref ip) + 1, head - 1, EvmExceptionType.OutOfGas);

                gas -= (nuint)(cost);
                vm.WorldState.Get(in storageCell, out slot);
                // Reloaded rather than held across the calls, where each would take a callee-saved register.
                handlers = state.OpcodeHandlers;
                code = ref stack.Code;
                sp = ref SlotAt(ref stack.Bottom, head);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<SLoadOpcode<OffFlag, Eip8038Off, OnFlag, OffFlag, Eip8360>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>SSTORE under EIP-2200 and EIP-2929 without EIP-8038, with the key and value read in their stack slots.</summary>
        /// <remarks>
        /// The shared handler copies both words off the stack and reads the fork's costs through the spec on every write.
        /// The costs and refunds are those of <see cref="EvmInstructions.InstructionSStoreMetered{TGasPolicy, TTracingInst, TUseNetGasStipendFix, TEip8037, Eip8038, Eip2929, Eip8279}"/>
        /// for the same flags. A static frame, a short stack, gas at or below the stipend sentry and traced refunds or
        /// storage run the shared handler instead, which faults or reports as it does.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteSStore(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            VirtualMachine<TGasPolicy> vm = state.Vm;
            VmState<TGasPolicy> frame = vm.VmState;
            if (head >= 2 && Covers(gas, GasCostOf.CallStipend + 1) && !frame.IsStatic && !vm.IsTracingRefunds && !vm.IsTracingOpLevelStorage)
            {
                vm.MetricsCounters.IncrementSStore();
                ref UInt256 key = ref Unsafe.As<byte, UInt256>(ref SlotBelow(ref sp, 1));
                ref UInt256 newValue = ref Unsafe.As<byte, UInt256>(ref SlotBelow(ref sp, 2));
                StorageCell storageCell = new(frame.Env.ExecutingAccount, in key);
                // Warming before the charge is unobservable: running out of gas halts the frame, whose restore drops the cell again.
                if (frame.AccessTracker.WarmUp(in storageCell) && !TryCharge(ref gas, GasCostOf.ColdSLoad))
                    goto OutOfGas;

                IWorldState worldState = vm.WorldState;
                // The guest's own world state reads the value, the original and the write through one lookup of the slot.
                IGuestSlotStore? slots = worldState as IGuestSlotStore;
                int slot = 0;
                UInt256 currentValue, originalValue;
                if (slots is not null)
                {
                    slot = slots.ReadForStore(in storageCell, out currentValue, out originalValue);
                }
                else
                {
                    worldState.Get(in storageCell, out currentValue);
                    Unsafe.SkipInit(out originalValue);
                }

                if (currentValue == newValue)
                {
                    if (!TryCharge(ref gas, vm.Spec.GasCosts.NetMeteredSStoreCost)) goto OutOfGas;
                }
                else
                {
                    SpecGasCosts costs = vm.Spec.GasCosts;
                    long sClearRefund = (long)costs.SClearRefund;
                    if (slots is null) worldState.GetOriginal(in storageCell, out originalValue);
                    if (originalValue == currentValue)
                    {
                        if (currentValue.IsZero)
                        {
                            if (!TryCharge(ref gas, GasCostOf.SSet)) goto OutOfGas;
                        }
                        else
                        {
                            if (!TryCharge(ref gas, costs.SStoreResetCost)) goto OutOfGas;
                            if (newValue.IsZero) frame.Refund += sClearRefund;
                        }
                    }
                    else
                    {
                        if (!TryCharge(ref gas, costs.NetMeteredSStoreCost)) goto OutOfGas;
                        bool originalIsZero = originalValue.IsZero;
                        if (!originalIsZero)
                        {
                            if (currentValue.IsZero) frame.Refund -= sClearRefund;
                            if (newValue.IsZero) frame.Refund += sClearRefund;
                        }

                        if (originalValue == newValue) frame.Refund += (long)costs.RefundFromReversal(originalIsZero);
                    }

                    if (slots is not null) slots.WriteAt(slot, in newValue);
                    else worldState.Set(in storageCell, in newValue, in currentValue);
                    if (newValue.IsZero) vm.MetricsCounters.IncrementStorageDeleted();
                }

                head -= 2;
                // Reloaded rather than held across the calls, where each would take a callee-saved register.
                handlers = state.OpcodeHandlers;
                code = ref stack.Code;
                sp = ref SlotAt(ref stack.Bottom, head);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);

            OutOfGas:
                return ExitChain(ref state, 0, (nint)Unsafe.ByteOffset(ref code, ref ip) + 1, head - 2, EvmExceptionType.OutOfGas);
            }

            nint shared = Entry(&ExecuteOpcode<SStoreMeteredOpcode<OffFlag, OnFlag, OffFlag, Eip8038Off, OnFlag, OffFlag, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>TLOAD, with the key read from and the value written to its stack slot.</summary>
        /// <remarks>
        /// The shared handler builds the storage cell twice and saves every callee-saved register around the world-state
        /// call. A short stack or gas runs the shared TLOAD handler instead, which faults on it.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteTLoad(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 0 && TryCharge(ref gas, TLoadGasCost.GasCost))
            {
                VirtualMachine<TGasPolicy> vm = state.Vm;
                ref UInt256 slot = ref Unsafe.As<byte, UInt256>(ref SlotBelow(ref sp, 1));
                StorageCell storageCell = new(vm.VmState.Env.ExecutingAccount, in slot);
                vm.WorldState.GetTransientState(in storageCell, out slot);
                // Reloaded rather than held across the call, where each would take a callee-saved register.
                handlers = state.OpcodeHandlers;
                code = ref stack.Code;
                sp = ref SlotAt(ref stack.Bottom, head);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<TLoadOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>TSTORE outside a static call, with the key and the value read from their stack slots.</summary>
        /// <remarks>
        /// The shared handler copies both words out and saves every callee-saved register around the world-state call. A
        /// static call or a short stack or gas runs the shared TSTORE handler instead, which faults on it.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteTStore(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            VirtualMachine<TGasPolicy> vm = state.Vm;
            VmState<TGasPolicy> frame = vm.VmState;
            if (head > 1 && !frame.IsStatic && TryCharge(ref gas, TStoreGasCost.GasCost))
            {
                ref UInt256 key = ref Unsafe.As<byte, UInt256>(ref SlotBelow(ref sp, 1));
                StorageCell storageCell = new(frame.Env.ExecutingAccount, in key);
                vm.WorldState.SetTransientState(in storageCell, in Unsafe.Subtract(ref key, 1));
                // Reloaded rather than held across the call, where each would take a callee-saved register.
                handlers = state.OpcodeHandlers;
                code = ref stack.Code;
                head -= 2;
                sp = ref SlotAt(ref stack.Bottom, head);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<TStoreOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>
        /// CALLDATACOPY or RETURNDATACOPY, as <typeparamref name="TSource"/> sources it, into a range that needs no new
        /// backing and leaves a gap of at most two words below it.
        /// </summary>
        /// <remarks>
        /// The shared handlers copy the three operands out of the stack and charge through 256-bit helpers. Every other
        /// case - short gas or stack, a length or destination of 2^32 or more, a range the backing cannot hold, or a
        /// return-data read past its end - runs the shared handler instead, which charges and faults on it.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteDataCopy<TSource>(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
            where TSource : struct, ICopySource
        {
            if (head > 2)
            {
                // The destination is the top word, the source offset the one below it and the length the third.
                ref ulong destination = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                ref ulong offset = ref Unsafe.Subtract(ref destination, LimbsPerWord);
                ref ulong length = ref Unsafe.Subtract(ref destination, 2 * LimbsPerWord);
                ulong size = length;
                ulong cost = VeryLowGasCost.GasCost + GasCostOf.Memory * ((size + (EvmStack.WordSize - 1)) >> 5);
                if ((Unsafe.Add(ref length, 1) | Unsafe.Add(ref length, 2) | Unsafe.Add(ref length, 3) | (size >> 32)) == 0 && Covers(gas, cost))
                {
                    ReadOnlySpan<byte> source = TSource.Read(ref stack, ref state);
                    ulong from = offset;
                    // From 2^32 the offset lies past any source, which only a zero-extending one reads.
                    bool offsetFits = (Unsafe.Add(ref offset, 1) | Unsafe.Add(ref offset, 2) | Unsafe.Add(ref offset, 3) | (from >> 32)) == 0;
                    bool inSource = offsetFits && from < (ulong)source.Length;
                    if (TSource.ZeroExtends || (offsetFits && from + size <= (ulong)source.Length))
                    {
                        if (size == 0)
                        {
                            gas -= (nuint)(cost);
                            goto Dispatch;
                        }

                        ulong target = destination;
                        if ((Unsafe.Add(ref destination, 1) | Unsafe.Add(ref destination, 2) | Unsafe.Add(ref destination, 3) | (target >> 32)) == 0)
                        {
                            gas -= (nuint)(cost);
                            ref byte range = ref state.Memory.TryPrepareRangeOverwrite(target, size, ref gas);
                            if (!Unsafe.IsNullRef(ref range))
                            {
                                uint copied = 0;
                                if (inSource)
                                {
                                    copied = (uint)Math.Min(size, (ulong)source.Length - from);
                                    Bytes.Copy(source.Slice((int)from, (int)copied), MemoryMarshal.CreateSpan(ref range, (int)copied));
                                }

                                Unsafe.InitBlockUnaligned(ref Unsafe.Add(ref range, copied), 0, (uint)size - copied);
                                // Reloaded rather than held across the calls, where each would take a callee-saved register.
                                handlers = state.OpcodeHandlers;
                                code = ref stack.Code;
                                sp = ref SlotAt(ref stack.Bottom, head);
                                goto Dispatch;
                            }

                            gas += (nuint)(cost);
                        }
                    }
                }
            }

            nint shared = TSource.SharedHandler;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);

        Dispatch:
            head -= 3;
            sp = ref Moved(ref sp, -3);
            ip = ref Unsafe.Add(ref ip, 1);
            nint next = AlignedTable(handlers)[PairAt(ref ip)];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
        }

        /// <summary>The bytes a copy to memory reads, as <see cref="ExecuteDataCopy{TSource}"/> runs it.</summary>
        internal interface ICopySource
        {
            /// <summary>The shared handler of the opcode, which handles every case that faults.</summary>
            static abstract nint SharedHandler { get; }

            /// <summary>Whether a read past the end of the source reads zeros rather than faulting.</summary>
            static abstract bool ZeroExtends { get; }

            /// <summary>The source's bytes.</summary>
            static abstract ReadOnlySpan<byte> Read(ref EvmStack stack, ref DispatchState state);
        }

        /// <summary>CALLDATACOPY: the input data, zero-extended.</summary>
        internal readonly struct CallDataSource : ICopySource
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<CallDataCopyOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);

            public static bool ZeroExtends => true;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ReadOnlySpan<byte> Read(ref EvmStack stack, ref DispatchState state) =>
                MemoryMarshal.CreateReadOnlySpan(in stack.InputData, (int)stack.InputDataLength);
        }

        /// <summary>RETURNDATACOPY: the last call's return data, which a read past its end faults on (EIP-211).</summary>
        internal readonly struct ReturnDataSource : ICopySource
        {
            public static nint SharedHandler =>
                Entry(&ExecuteOpcode<ReturnDataCopyOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);

            public static bool ZeroExtends => false;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ReadOnlySpan<byte> Read(ref EvmStack stack, ref DispatchState state) => state.Vm.ReturnDataBuffer.Span;
        }

        /// <summary>MSTORE8 of a byte inside the active, initialized memory.</summary>
        /// <remarks>
        /// Every other case - short gas or stack, a byte that grows memory or lies past the initialized memory, an offset
        /// of 2^32 or more - runs the shared MSTORE8 handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteMStore8InsideActiveMemory(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 1 && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong offset = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                if ((Unsafe.Add(ref offset, 1) | Unsafe.Add(ref offset, 2) | Unsafe.Add(ref offset, 3) | (offset >> 32)) == 0)
                {
                    ref byte destination = ref state.Memory.GetActiveInitializedRange(offset, 1);
                    if (!Unsafe.IsNullRef(ref destination))
                    {
                        // The value is the word below the offset, whose low byte comes first in limb layout.
                        destination = (byte)Unsafe.Add(ref offset, -LimbsPerWord);
                        head -= 2;
                        sp = ref Moved(ref sp, -2);
                        ip = ref Unsafe.Add(ref ip, 1);
                        nint next = AlignedTable(handlers)[PairAt(ref ip)];
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
                    }
                }

                gas += (nuint)(VeryLowGasCost.GasCost);
            }

            nint shared = Entry(&ExecuteOpcode<MStore8Opcode<OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>MLOAD of a word inside the active, initialized memory.</summary>
        /// <remarks>
        /// Every other case - short gas or stack, a word that grows memory or lies past the initialized memory, an offset
        /// of 2^32 or more - runs the shared MLOAD handler instead. Loads rarely grow memory, and charging an expansion
        /// here would keep enough values live to take callee-saved registers on every load.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteMLoadFromActiveMemory(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 0 && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong slot = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                if ((Unsafe.Add(ref slot, 1) | Unsafe.Add(ref slot, 2) | Unsafe.Add(ref slot, 3) | (slot >> 32)) == 0)
                {
                    ref byte source = ref state.Memory.GetActiveInitializedRange(slot, EvmStack.WordSize);
                    if (!Unsafe.IsNullRef(ref source))
                    {
                        // Memory holds the word big-endian; the slot takes it in limb layout, over the offset it held.
                        LoadBigEndian(ref slot, ref source);
                        ip = ref Unsafe.Add(ref ip, 1);
                        nint next = AlignedTable(handlers)[PairAt(ref ip)];
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
                    }
                }

                gas += (nuint)(VeryLowGasCost.GasCost);
            }

            nint shared = Entry(&ExecuteOpcode<MLoadOpcode<OffFlag, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>CALLDATALOAD of a word that lies wholly inside the input data.</summary>
        /// <remarks>
        /// Every other case - short gas or stack, a word that runs past the end of the input data - runs the shared
        /// CALLDATALOAD handler instead, which zero-pads it. That handler calls out for the padding, and so saves the
        /// callee-saved registers on every load.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteCallDataLoadOfWholeWord(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 0 && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong slot = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                ulong offset = slot;
                // Below 2^32 the offset cannot overflow when the word is added to it.
                if ((Unsafe.Add(ref slot, 1) | Unsafe.Add(ref slot, 2) | Unsafe.Add(ref slot, 3) | (offset >> 32)) == 0 &&
                    offset + EvmStack.WordSize <= (ulong)stack.InputDataLength)
                {
                    // The input holds the word big-endian; the slot takes it in limb layout, over the offset it held.
                    ref byte source = ref Unsafe.Add(ref Unsafe.AsRef(in stack.InputData), (nint)offset);
                    LoadBigEndian(ref slot, ref source);
                    ip = ref Unsafe.Add(ref ip, 1);
                    nint next = AlignedTable(handlers)[PairAt(ref ip)];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
                }

                gas += (nuint)(VeryLowGasCost.GasCost);
            }

            nint shared = Entry(&ExecuteOpcode<CallDataLoadOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>KECCAK256 of a range inside the active, initialized memory.</summary>
        /// <remarks>
        /// Every other case - short gas or stack, a range that grows memory or lies past the initialized memory, an
        /// offset or length of 2^32 or more - runs the shared KECCAK256 handler instead. Hashing takes a call either way,
        /// but this handler holds only five values across it, where the shared one saves every callee-saved register.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteKeccak256OfActiveMemory(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 1)
            {
                ref ulong offsetLimbs = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                ref ulong lengthLimbs = ref Unsafe.Subtract(ref offsetLimbs, EvmStack.WordSize / sizeof(ulong));
                ulong offset = offsetLimbs;
                ulong length = lengthLimbs;
                // Below 2^32 neither the word count nor the end of the range can overflow.
                if ((Unsafe.Add(ref offsetLimbs, 1) | Unsafe.Add(ref offsetLimbs, 2) | Unsafe.Add(ref offsetLimbs, 3) |
                     Unsafe.Add(ref lengthLimbs, 1) | Unsafe.Add(ref lengthLimbs, 2) | Unsafe.Add(ref lengthLimbs, 3) |
                     ((offset | length) >> 32)) == 0)
                {
                    ulong cost = GasCostOf.Sha3 + GasCostOf.Sha3Word * ((length + (EvmStack.WordSize - 1)) >> 5);
                    ref byte data = ref state.Memory.GetActiveInitializedRange(offset, length);
                    if (Covers(gas, cost) && !Unsafe.IsNullRef(ref data))
                    {
                        gas -= (nuint)(cost);
                        head--;
                        if (TryReadWordKeccakMemo(ref data, length, ref lengthLimbs))
                        {
                            sp = ref Moved(ref sp, -1);
                            ip = ref Unsafe.Add(ref ip, 1);
                            nint memoized = AlignedTable(handlers)[PairAt(ref ip)];
                            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, memoized);
                        }

                        KeccakCache.ComputeTo(MemoryMarshal.CreateReadOnlySpan(ref data, (int)length), out Unsafe.As<ulong, ValueHash256>(ref lengthLimbs));
                        // Reloaded rather than held across the call, where each would take a callee-saved register.
                        handlers = state.OpcodeHandlers;
                        code = ref stack.Code;
                        sp = ref SlotAt(ref stack.Bottom, head);
                        // The hash lands big-endian over the length; the slot holds it in limb layout.
                        ref ulong hash = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                        ulong limb3 = ZkEvmBitOperations.Bswap64(hash);
                        ulong limb2 = ZkEvmBitOperations.Bswap64(Unsafe.Add(ref hash, 1));
                        ulong limb1 = ZkEvmBitOperations.Bswap64(Unsafe.Add(ref hash, 2));
                        ulong limb0 = ZkEvmBitOperations.Bswap64(Unsafe.Add(ref hash, 3));
                        hash = limb0;
                        Unsafe.Add(ref hash, 1) = limb1;
                        Unsafe.Add(ref hash, 2) = limb2;
                        Unsafe.Add(ref hash, 3) = limb3;
                        WriteWordKeccakMemo(ref state.Memory.GetActiveInitializedRange(offset, length), length, ref hash);
                        ip = ref Unsafe.Add(ref ip, 1);
                        nint next = AlignedTable(handlers)[PairAt(ref ip)];
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
                    }
                }
            }

            nint shared = Entry(&ExecuteOpcode<KeccakOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>The bits of the index of an entry of <see cref="s_wordKeccakMemo"/>.</summary>
        private const int WordKeccakMemoBits = 12;

        /// <summary>The longs of an entry of <see cref="s_wordKeccakMemo"/>: the input, at most 64 bytes, its digest in limb layout, and its length.</summary>
        private const int WordKeccakMemoEntryLongs = 16;

        /// <summary>
        /// The digests of 32- and 64-byte KECCAK256 inputs, keyed by the input, held in the limb layout of the stack slot they
        /// land in; a length of zero marks an empty entry.
        /// </summary>
        /// <remarks>
        /// Contracts hash the same storage keys - a mapping's key and slot, an array's slot - again and again. Finding the
        /// digest here takes a mix of four input words, a compare of the input and a copy, where the general memo also
        /// reverses the digest's bytes for the slot on every hit.
        /// </remarks>
        private static ulong* s_wordKeccakMemo;

        /// <summary>Copies the memoized digest of the <paramref name="length"/> bytes at <paramref name="data"/> to <paramref name="slot"/> when there is one.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadWordKeccakMemo(ref byte data, ulong length, ref ulong slot)
        {
            ulong* memo = s_wordKeccakMemo;
            if (memo is null || (length != 32 && length != 64) || ((nuint)Unsafe.AsPointer(ref data) & (nuint)(sizeof(ulong) - 1)) != 0)
                return false;

            ref ulong key = ref Unsafe.As<byte, ulong>(ref data);
            ulong* entry = WordKeccakMemoEntry(memo, ref key, length);
            if (entry[12] != length) return false;
            ulong difference = (entry[0] ^ key) | (entry[1] ^ Unsafe.Add(ref key, 1)) | (entry[2] ^ Unsafe.Add(ref key, 2)) | (entry[3] ^ Unsafe.Add(ref key, 3));
            if (length == 64)
                difference |= (entry[4] ^ Unsafe.Add(ref key, 4)) | (entry[5] ^ Unsafe.Add(ref key, 5)) | (entry[6] ^ Unsafe.Add(ref key, 6)) | (entry[7] ^ Unsafe.Add(ref key, 7));
            if (difference != 0) return false;

            slot = entry[8];
            Unsafe.Add(ref slot, 1) = entry[9];
            Unsafe.Add(ref slot, 2) = entry[10];
            Unsafe.Add(ref slot, 3) = entry[11];
            return true;
        }

        /// <summary>Memoizes <paramref name="digest"/>, in limb layout, as the digest of the <paramref name="length"/> bytes at <paramref name="data"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteWordKeccakMemo(ref byte data, ulong length, ref ulong digest)
        {
            if ((length != 32 && length != 64) || Unsafe.IsNullRef(ref data) || ((nuint)Unsafe.AsPointer(ref data) & (nuint)(sizeof(ulong) - 1)) != 0)
                return;

            ulong* memo = s_wordKeccakMemo;
            if (memo is null)
                s_wordKeccakMemo = memo = (ulong*)NativeMemory.AllocZeroed((nuint)((1 << WordKeccakMemoBits) * WordKeccakMemoEntryLongs * sizeof(ulong)));
            ref ulong key = ref Unsafe.As<byte, ulong>(ref data);
            ulong* entry = WordKeccakMemoEntry(memo, ref key, length);
            for (int i = 0; i < (int)(length >> 3); i++) entry[i] = Unsafe.Add(ref key, i);
            for (int i = 0; i < 4; i++) entry[8 + i] = Unsafe.Add(ref digest, i);
            entry[12] = length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong* WordKeccakMemoEntry(ulong* memo, ref ulong key, ulong length)
        {
            ulong mix = key ^ Unsafe.Add(ref key, 1) ^ Unsafe.Add(ref key, (nint)(length >> 3) - 2) ^ Unsafe.Add(ref key, (nint)(length >> 3) - 1);
            uint index = ((uint)mix ^ (uint)(mix >> 32)) * 0x9E3779B1u >> (32 - WordKeccakMemoBits);
            return memo + index * WordKeccakMemoEntryLongs;
        }

        /// <summary>SHL with the shift in line; an amount of 256 or more clears the word.</summary>
        /// <remarks>A short stack or gas runs the shared SHL handler instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteShl(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 1 && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                ShiftLeftStep.Apply(ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0)), ref ip);

                head--;
                sp = ref Moved(ref sp, -1);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<ShiftOpcode<EvmInstructions.OpShl, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>SHR with the shift in line; an amount of 256 or more clears the word.</summary>
        /// <remarks>A short stack or gas runs the shared SHR handler instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteShr(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 1 && ChargeUntested(ref gas, VeryLowGasCost.GasCost))
            {
                ShiftRightStep.Apply(ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 0)), ref ip);

                head--;
                sp = ref Moved(ref sp, -1);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<ShiftOpcode<EvmInstructions.OpShr, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>MUL of the top two stack words.</summary>
        /// <remarks>
        /// Factors below 2^64 multiply here. Wider factors go to <see cref="ExecuteMulOfHalfWidthFactors"/> or <see cref="ExecuteMulOfWideFactors"/>,
        /// so this handler needs no frame; in the SP1 and OpenVM guests every product goes to <see cref="GuestMul.MultiplyAndDispatch"/>.
        /// A short stack or gas runs the shared MUL handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteMul(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 1 && ChargeUntested(ref gas, LowGasCost.GasCost))
            {
                if (GuestMulFlag.IsActive)
                {
                    nint native = (nint)(delegate*<void>)&GuestMul.MultiplyAndDispatch;
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, native);
                }

                // The product replaces the second word.
                ref ulong product = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 2));
                ref ulong top = ref Unsafe.Add(ref product, LimbsPerWord);
                if (AirbenderFlag.IsActive)
                {
                    AirbenderArithmetic.MultiplyLow(Unsafe.AsPointer(ref product), Unsafe.AsPointer(ref top));
                    head--;
                    sp = ref Moved(ref sp, -1);
                    ip = ref Unsafe.Add(ref ip, 1);
                    nint following = AlignedTable(handlers)[PairAt(ref ip)];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, following);
                }

                ulong upperHalves = Unsafe.Add(ref top, 2) | Unsafe.Add(ref top, 3) | Unsafe.Add(ref product, 2) | Unsafe.Add(ref product, 3);
                if ((Unsafe.Add(ref top, 1) | Unsafe.Add(ref product, 1) | upperHalves) != 0)
                {
                    nint wide = upperHalves == 0
                        ? Entry(&ExecuteMulOfHalfWidthFactors)
                        : Entry(&ExecuteMulOfWideFactors);
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, wide);
                }

                ulong left = top;
                ulong right = product;
                product = left * right;
                Unsafe.Add(ref product, 1) = (left | right) >> 32 == 0 ? 0 : MultiplyHighWithoutFrame(left, right);
                head--;
                sp = ref Moved(ref sp, -1);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<Math2Opcode<EvmInstructions.OpMul, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>The rest of <see cref="ExecuteMul"/> for factors below 2^128, at least one of which is 2^64 or more.</summary>
        /// <remarks>
        /// Entered only from there, with the stack checked and the gas charged. The ZisK guest hands the product to its
        /// 256-bit arithmetic, which takes fewer steps than the four 64-bit products every other guest takes.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteMulOfHalfWidthFactors(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            ref ulong product = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 2));
            ref ulong top = ref Unsafe.Add(ref product, LimbsPerWord);
            if (ZiskFlag.IsActive)
            {
                // The result must not alias an operand; the stack is pinned.
                UInt256 result;
                ulong* right = (ulong*)Unsafe.AsPointer(ref product);
                ZiskArithmetic.MultiplyLow((ulong*)Unsafe.AsPointer(ref top), right, (ulong*)&result);
                *(UInt256*)right = result;
            }
            else
            {
                // Nothing is truncated, so the top limb takes no carry out.
                ulong a0 = top, a1 = Unsafe.Add(ref top, 1);
                ulong b0 = product, b1 = Unsafe.Add(ref product, 1);
                ulong low01 = a0 * b1;
                ulong low10 = a1 * b0;
                ulong r1 = MultiplyHigh(a0, b0) + low01;
                ulong carry = r1 < low01 ? 1UL : 0UL;
                r1 += low10;
                carry += r1 < low10 ? 1UL : 0UL;
                ulong high10 = MultiplyHigh(a1, b0);
                ulong low11 = a1 * b1;
                ulong r2 = MultiplyHigh(a0, b1) + high10;
                ulong carry2 = r2 < high10 ? 1UL : 0UL;
                r2 += low11;
                carry2 += r2 < low11 ? 1UL : 0UL;
                r2 += carry;
                carry2 += r2 < carry ? 1UL : 0UL;
                product = a0 * b0;
                Unsafe.Add(ref product, 1) = r1;
                Unsafe.Add(ref product, 2) = r2;
                Unsafe.Add(ref product, 3) = MultiplyHigh(a1, b1) + carry2;
            }

            head--;
            sp = ref Moved(ref sp, -1);
            ip = ref Unsafe.Add(ref ip, 1);
            nint next = AlignedTable(handlers)[PairAt(ref ip)];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
        }

        /// <summary>The rest of <see cref="ExecuteMul"/> for factors at least one of which is 2^128 or more.</summary>
        /// <remarks>
        /// Entered only from there, with the stack checked and the gas charged. Takes the ten 64-bit products that
        /// reach the low 256 bits of the product, row by row over the second factor's limbs; the ZisK guest takes the
        /// low 256 bits of the whole product from its 256-bit arithmetic instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteMulOfWideFactors(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            ref ulong product = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 2));
            ref ulong top = ref Unsafe.Add(ref product, LimbsPerWord);
            ulong a0 = top, a1 = Unsafe.Add(ref top, 1), a2 = Unsafe.Add(ref top, 2), a3 = Unsafe.Add(ref top, 3);
            ulong b0 = product, b1 = Unsafe.Add(ref product, 1), b2 = Unsafe.Add(ref product, 2), b3 = Unsafe.Add(ref product, 3);
            ulong r0, r1, r2, r3;
            if (ZiskFlag.IsActive)
            {
                UInt256 low;
                ZiskArithmetic.MultiplyLow((ulong*)Unsafe.AsPointer(ref top), (ulong*)Unsafe.AsPointer(ref product), (ulong*)&low);
                r0 = low.u0;
                r1 = low.u1;
                r2 = low.u2;
                r3 = low.u3;
            }
            else
            {
                r0 = a0 * b0;
                ulong carry = MultiplyHigh(a0, b0);
                r1 = MultiplyAdd(a1, b0, carry, out carry);
                r2 = MultiplyAdd(a2, b0, carry, out carry);
                r3 = a3 * b0 + carry;

                r1 = MultiplyAccumulate(a0, b1, r1, 0, out carry);
                r2 = MultiplyAccumulate(a1, b1, r2, carry, out carry);
                r3 += a2 * b1 + carry;

                r2 = MultiplyAccumulate(a0, b2, r2, 0, out carry);
                r3 += a1 * b2 + carry + a0 * b3;
            }

            product = r0;
            Unsafe.Add(ref product, 1) = r1;
            Unsafe.Add(ref product, 2) = r2;
            Unsafe.Add(ref product, 3) = r3;
            head--;
            sp = ref Moved(ref sp, -1);
            ip = ref Unsafe.Add(ref ip, 1);
            nint next = AlignedTable(handlers)[PairAt(ref ip)];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
        }

        /// <summary>The low limb of <paramref name="left"/> times <paramref name="right"/> plus <paramref name="addend"/>, with the high limb in <paramref name="high"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MultiplyAdd(ulong left, ulong right, ulong addend, out ulong high)
        {
            ulong low = left * right + addend;
            high = MultiplyHigh(left, right) + (low < addend ? 1UL : 0UL);
            return low;
        }

        /// <summary>
        /// The low limb of <paramref name="left"/> times <paramref name="right"/> plus <paramref name="accumulator"/> and
        /// <paramref name="carry"/>, with the high limb in <paramref name="high"/>; the sum fits 128 bits.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MultiplyAccumulate(ulong left, ulong right, ulong accumulator, ulong carry, out ulong high)
        {
            ulong low = left * right + carry;
            ulong carryOut = low < carry ? 1UL : 0UL;
            low += accumulator;
            carryOut += low < accumulator ? 1UL : 0UL;
            high = MultiplyHigh(left, right) + carryOut;
            return low;
        }

        /// <summary>The high 64 bits of the product of <paramref name="left"/> and <paramref name="right"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MultiplyHigh(ulong left, ulong right) => Math.BigMul(left, right, out _);

        /// <inheritdoc cref="MultiplyHigh"/>
        /// <remarks><see cref="Math.BigMul(ulong, ulong, out ulong)"/> takes one instruction, but gives the frameless MUL handler a frame.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MultiplyHighWithoutFrame(ulong left, ulong right)
        {
            ulong leftLow = (uint)left;
            ulong leftHigh = left >> 32;
            ulong rightLow = (uint)right;
            ulong rightHigh = right >> 32;
            // Each half is last used as early as it can be, so the handler's common case fits the temporary registers.
            ulong middle = (leftLow * rightLow) >> 32;
            ulong lowHigh = leftLow * rightHigh;
            ulong highLow = leftHigh * rightLow;
            // At most three 32-bit values, so the carry into the high half stays in its own bits.
            middle = middle + (uint)lowHigh + (uint)highLow;
            return leftHigh * rightHigh + (lowHigh >> 32) + (highLow >> 32) + (middle >> 32);
        }

        /// <summary>DIV of a dividend below 2^64 by a nonzero divisor below 2^64, or by a power of two, which it shifts out.</summary>
        /// <remarks>
        /// Either takes one instruction or a shift, where the shared handler runs a full 256-bit division.
        /// Every other division goes to <see cref="ExecuteDivOfWideOperands"/>; a short stack or gas runs the shared DIV handler.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteDiv(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            if (head > 1 && ChargeUntested(ref gas, LowGasCost.GasCost))
            {
                // The dividend is the top word; the quotient replaces the divisor below it.
                ref ulong quotient = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 2));
                ref ulong dividend = ref Unsafe.Add(ref quotient, LimbsPerWord);
                ulong divisor = quotient;
                if ((Unsafe.Add(ref dividend, 1) | Unsafe.Add(ref dividend, 2) | Unsafe.Add(ref dividend, 3) |
                     Unsafe.Add(ref quotient, 1) | Unsafe.Add(ref quotient, 2) | Unsafe.Add(ref quotient, 3)) == 0 && divisor != 0)
                {
                    quotient = dividend / divisor;
                }
                else if (TryGetLog2(ref quotient, out int shift))
                {
                    CopyWord(ref dividend, ref quotient);
                    ShiftRight(ref quotient, shift);
                }
                else
                {
                    nint wide = Entry(&ExecuteDivOfWideOperands);
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, wide);
                }

                head--;
                sp = ref Moved(ref sp, -1);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = Entry(&ExecuteOpcode<Math2Opcode<EvmInstructions.OpDiv, OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>The rest of <see cref="ExecuteDiv"/>, for a zero divisor, or wider operands whose divisor is not a power of two.</summary>
        /// <remarks>
        /// Entered only from there, with the stack checked and the gas charged. A zero divisor leaves the zero its slot
        /// holds. In the ZisK guest a dividend below the divisor gives a zero quotient without
        /// entering its 256-bit division; elsewhere <see cref="UInt256.Divide(in UInt256, in UInt256, out UInt256)"/>
        /// compares the operands itself.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteDivOfWideOperands(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            ref ulong quotient = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 2));
            if ((quotient | Unsafe.Add(ref quotient, 1) | Unsafe.Add(ref quotient, 2) | Unsafe.Add(ref quotient, 3)) != 0)
            {
                if (ZiskFlag.IsActive && IsBelow(ref quotient, LimbsPerWord, 0))
                {
                    SetWord(ref quotient, 0);
                }
                else
                {
                    ref UInt256 divisor = ref Unsafe.As<ulong, UInt256>(ref quotient);
                    // The quotient must not alias an operand.
                    EvmInstructions.Divide256(in Unsafe.Add(ref divisor, 1), in divisor, out UInt256 result);
                    divisor = result;

                    // Reloaded rather than held across the call, where each would take a callee-saved register.
                    handlers = state.OpcodeHandlers;
                    code = ref stack.Code;
                    sp = ref SlotAt(ref stack.Bottom, head);
                }
            }

            head--;
            sp = ref Moved(ref sp, -1);
            ip = ref Unsafe.Add(ref ip, 1);
            nint next = AlignedTable(handlers)[PairAt(ref ip)];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
        }

        /// <summary>JUMP onto a destination the incremental bitmap already holds, with the JUMPDEST it lands on.</summary>
        /// <remarks>
        /// A destination inside the code that the bitmap does not hold yet goes to
        /// <see cref="ExecuteJumpToUnanalyzedDestination{TConditional}"/>; every other case - a short stack or gas, or a
        /// destination outside the code - runs the shared JUMP handler, which charges and faults exactly as
        /// the traced tables do.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteJumpToAnalyzedDestination(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            ulong jumpAndJumpDestGas = JumpGasCost.GasCost + JumpDestGasCost.GasCost;
            if (head > 0 && TryCharge(ref gas, jumpAndJumpDestGas))
            {
                ref ulong destination = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                nuint target = (nuint)destination;
                if (FitsNativeWord(ref destination) &&
                    target < (nuint)AlignedStack(ref stack).CodeLength)
                {
                    if (!AlignedStack(ref stack).IsAnalyzedJumpDestination(target))
                    {
                        gas += (nuint)(jumpAndJumpDestGas);
                        nint analyze = Entry(&ExecuteJumpToUnanalyzedDestination<OffFlag>);
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, analyze);
                    }

                    head--;
                    sp = ref Moved(ref sp, -1);
                    ip = ref Unsafe.Add(ref code, (nint)target + 1);
                    nint next = AlignedTable(handlers)[PairAt(ref ip)];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
                }

                gas += (nuint)(jumpAndJumpDestGas);
            }

            nint shared = Entry(&ExecuteOpcode<JumpOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>JUMPI that falls through, or that jumps onto a destination the incremental bitmap already holds.</summary>
        /// <remarks>
        /// A taken jump also runs the JUMPDEST it lands on. A taken jump into the code that the bitmap does not
        /// hold yet goes to <see cref="ExecuteJumpToUnanalyzedDestination{TConditional}"/>; every other case runs the shared
        /// JUMPI handler.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteJumpIfToAnalyzedDestination(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
        {
            ulong jumpIAndJumpDestGas = JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
            if (head > 1 && Covers(gas, JumpIGasCost.GasCost))
            {
                ref ulong destination = ref Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
                ref ulong condition = ref Unsafe.Subtract(ref destination, EvmStack.WordSize / sizeof(ulong));
                if (IsZeroWord(ref condition))
                {
                    head -= 2;
                    sp = ref Moved(ref sp, -2);
                    gas -= (nuint)(JumpIGasCost.GasCost);
                    ip = ref Unsafe.Add(ref ip, 1);
                    nint notTaken = AlignedTable(handlers)[PairAt(ref ip)];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, notTaken);
                }

                nuint target = (nuint)destination;
                if (Covers(gas, jumpIAndJumpDestGas) &&
                    FitsNativeWord(ref destination) &&
                    target < (nuint)AlignedStack(ref stack).CodeLength)
                {
                    if (!AlignedStack(ref stack).IsAnalyzedJumpDestination(target))
                    {
                        nint analyze = Entry(&ExecuteJumpToUnanalyzedDestination<OnFlag>);
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, analyze);
                    }

                    head -= 2;
                    sp = ref Moved(ref sp, -2);
                    gas -= (nuint)(jumpIAndJumpDestGas);
                    ip = ref Unsafe.Add(ref code, (nint)target + 1);
                    nint taken = AlignedTable(handlers)[PairAt(ref ip)];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, taken);
                }
            }

            nint shared = Entry(&ExecuteJumpIfOpcode<OffFlag, OffFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, shared);
        }

        /// <summary>
        /// The rest of <see cref="ExecuteJumpToAnalyzedDestination"/> or, with <typeparamref name="TConditional"/>,
        /// <see cref="ExecuteJumpIfToAnalyzedDestination"/>, for a taken jump whose destination the bitmap does not hold yet.
        /// </summary>
        /// <remarks>
        /// Entered only from there and from the guest handlers that fuse a jump with the opcodes before it, with the stack,
        /// gas, condition and range checks passed. Most destinations are proven by the 32 bytes before them, which takes
        /// no call, so this handler has no frame either; the rest go to
        /// <see cref="ExecuteJumpToScannedDestination{TConditional}"/>.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteJumpToUnanalyzedDestination<TConditional>(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
            where TConditional : struct, IFlag
        {
            nint target = (nint)Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
            Debug.Assert((nuint)target < (nuint)stack.CodeLength);
            if (stack.TryMarkJumpDestination(target, ref code))
            {
                head -= TConditional.IsActive ? 2 : 1;
                sp = ref Moved(ref sp, -(TConditional.IsActive ? 2 : 1));
                gas -= (nuint)(JumpAndJumpDestGas<TConditional>());
                ip = ref Unsafe.Add(ref code, target + 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint scan = Entry(&ExecuteJumpToScannedDestination<TConditional>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, scan);
        }

        /// <summary>The rest of <see cref="ExecuteJumpToUnanalyzedDestination{TConditional}"/> for a destination a single look-back cannot decide.</summary>
        /// <remarks>
        /// The analysis is inlined here and needs callee-saved registers, so this handler has a frame and the handlers
        /// before it do not pay for it. An invalid destination runs the shared jump handler, which faults on it.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteJumpToScannedDestination<TConditional>(
            ref EvmStack stack,
            nuint gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte sp)
            where TConditional : struct, IFlag
        {
            nint target = (nint)Unsafe.As<byte, ulong>(ref SlotBelow(ref sp, 1));
            if (stack.AnalyzeJumpDestination(target, ref code))
            {
                // Reloaded rather than held across the inlined analysis, which needs the callee-saved registers.
                handlers = state.OpcodeHandlers;
                code = ref stack.Code;
                head -= TConditional.IsActive ? 2 : 1;
                sp = ref SlotAt(ref stack.Bottom, head);
                gas -= (nuint)(JumpAndJumpDestGas<TConditional>());
                ip = ref Unsafe.Add(ref code, target + 1);
                nint next = AlignedTable(handlers)[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref sp, next);
            }

            nint shared = TConditional.IsActive
                ? Entry(&ExecuteJumpIfOpcode<OffFlag, OffFlag>)
                : Entry(&ExecuteOpcode<JumpOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, state.OpcodeHandlers, ref stack.Code, ref SlotAt(ref stack.Bottom, head), shared);
        }

        /// <summary>The charge of a taken JUMP, or with <typeparamref name="TConditional"/> a taken JUMPI, and the JUMPDEST it lands on.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong JumpAndJumpDestGas<TConditional>() where TConditional : struct, IFlag =>
            (TConditional.IsActive ? JumpIGasCost.GasCost : JumpGasCost.GasCost) + JumpDestGasCost.GasCost;
    }
}
