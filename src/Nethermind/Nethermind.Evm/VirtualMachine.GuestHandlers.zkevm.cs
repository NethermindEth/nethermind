// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
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

        lookup[(int)Instruction.PUSH1] = AsTableEntry(&RawCalliHelper.ExecutePush1);
        lookup[(int)Instruction.DUP1] = AsTableEntry(&RawCalliHelper.ExecuteDup1);
        lookup[(int)Instruction.ISZERO] = AsTableEntry(&RawCalliHelper.ExecuteCondition<RawCalliHelper.IsZeroCondition>);
        lookup[(int)Instruction.EQ] = AsTableEntry(&RawCalliHelper.ExecuteCondition<RawCalliHelper.EqualCondition>);
        lookup[(int)Instruction.LT] = AsTableEntry(&RawCalliHelper.ExecuteCondition<RawCalliHelper.LessThanCondition>);
        lookup[(int)Instruction.GT] = AsTableEntry(&RawCalliHelper.ExecuteCondition<RawCalliHelper.GreaterThanCondition>);
        lookup[(int)Instruction.PUSH2] = AsTableEntry(&RawCalliHelper.ExecutePush2);
        lookup[(int)Instruction.JUMP] = AsTableEntry(&RawCalliHelper.ExecuteJumpToAnalyzedDestination);
        lookup[(int)Instruction.JUMPI] = AsTableEntry(&RawCalliHelper.ExecuteJumpIfToAnalyzedDestination);
        lookup[(int)Instruction.MLOAD] = AsTableEntry(&RawCalliHelper.ExecuteMLoadFromActiveMemory);
        lookup[(int)Instruction.MSTORE] = AsTableEntry(&RawCalliHelper.ExecuteMStoreWithoutClearing);
        lookup[(int)Instruction.CALLDATALOAD] = AsTableEntry(&RawCalliHelper.ExecuteCallDataLoadOfWholeWord);
        lookup[(int)Instruction.KECCAK256] = AsTableEntry(&RawCalliHelper.ExecuteKeccak256OfActiveMemory);
        if (spec.ShiftOpcodesEnabled)
        {
            lookup[(int)Instruction.SHL] = AsTableEntry(&RawCalliHelper.ExecuteShl);
            lookup[(int)Instruction.SHR] = AsTableEntry(&RawCalliHelper.ExecuteShr);
        }
    }

    private static partial class RawCalliHelper
    {
        /// <summary>Marks a direct tail transfer that the opcode weaver emits into the calling handler.</summary>
        /// <remarks>The target is last to match the calli evaluation stack; this method must never survive weaving.</remarks>
        private static EvmExceptionType TailDispatch(
            ref EvmStack stack, ulong gas, ref DispatchState state, nint pc, nint head,
            nint* handlers, ref byte code, nint codeLength, nint target) =>
            throw new InvalidOperationException("Guest tail dispatch was not woven.");

        /// <summary>
        /// A comparison, fused with the branch after it - <c>PUSH2</c> <c>JUMPI</c>, or <c>ISZERO</c> <c>PUSH2</c> <c>JUMPI</c> -
        /// whenever that branch falls through or lands on a destination the incremental bitmap already holds.
        /// </summary>
        /// <remarks>
        /// Compilers branch on nearly every comparison they emit, so fusing saves the comparison its result slot and
        /// the push its dispatch. The fused step charges every opcode it covers; with too little gas for all of them,
        /// or a branch it cannot take in line, the comparison runs alone and the opcodes after it run as they would.
        /// A short stack or gas runs the shared handler of the comparison instead, which faults on it.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteCondition<TCondition>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
            where TCondition : struct, IStackCondition
        {
            if (head >= TCondition.Inputs && gas >= VeryLowGasCost.GasCost)
            {
                ref ulong top = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                bool condition = TCondition.Evaluate(ref top);
                // The branch's PUSH2 overflows a stack the comparison leaves full, which only ISZERO can.
                if (TCondition.Inputs > 1 || head < EvmStack.MaxStackSize - 1)
                {
                    // The four bytes after the comparison, read at once: a PUSH2, its two immediates and a JUMPI.
                    uint branch = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref code, pc + 1));
                    if ((byte)branch == (byte)Instruction.PUSH2 && branch >> 24 == (byte)Instruction.JUMPI)
                    {
                        if (!condition)
                        {
                            ulong notTakenGas = 2 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost;
                            if (gas >= notTakenGas)
                            {
                                head -= TCondition.Inputs;
                                gas -= notTakenGas;
                                pc += 5;
                                goto Dispatch;
                            }
                        }
                        else
                        {
                            ulong takenGas = 2 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                            nuint destination = BinaryPrimitives.ReverseEndianness((ushort)(branch >> 8));
                            if (gas >= takenGas && destination < (nuint)codeLength && stack.IsAnalyzedJumpDestination(destination))
                            {
                                head -= TCondition.Inputs;
                                gas -= takenGas;
                                pc = (nint)destination + 1;
                                goto Dispatch;
                            }
                        }
                    }
                    // An ISZERO between the comparison and the branch only inverts what the branch tests.
                    else if ((byte)branch == (byte)Instruction.ISZERO)
                    {
                        branch = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref code, pc + 2));
                        if ((byte)branch == (byte)Instruction.PUSH2 && branch >> 24 == (byte)Instruction.JUMPI)
                        {
                            if (condition)
                            {
                                ulong notTakenGas = 3 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost;
                                if (gas >= notTakenGas)
                                {
                                    head -= TCondition.Inputs;
                                    gas -= notTakenGas;
                                    pc += 6;
                                    goto Dispatch;
                                }
                            }
                            else
                            {
                                ulong takenGas = 3 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                                nuint destination = BinaryPrimitives.ReverseEndianness((ushort)(branch >> 8));
                                if (gas >= takenGas && destination < (nuint)codeLength && stack.IsAnalyzedJumpDestination(destination))
                                {
                                    head -= TCondition.Inputs;
                                    gas -= takenGas;
                                    pc = (nint)destination + 1;
                                    goto Dispatch;
                                }
                            }
                        }
                    }
                }

                // Unfused: the result replaces the deepest input, in limb layout.
                ref ulong result = ref Unsafe.Subtract(ref top, (TCondition.Inputs - 1) * (EvmStack.WordSize / sizeof(ulong)));
                SetWord(ref result, condition ? 1UL : 0UL);
                head -= TCondition.Inputs - 1;
                gas -= VeryLowGasCost.GasCost;
                pc++;
                goto Dispatch;
            }

            nint shared = TCondition.SharedHandler;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);

        Dispatch:
            nint next = handlers[Unsafe.Add(ref code, pc)];
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
        }

        /// <summary>A comparison of the words on top of the stack, as <see cref="ExecuteCondition{TCondition}"/> runs it.</summary>
        internal interface IStackCondition
        {
            /// <summary>How many words the comparison consumes.</summary>
            static abstract int Inputs { get; }

            /// <summary>The shared handler of the comparison, which handles every case that faults.</summary>
            static abstract nint SharedHandler { get; }

            /// <summary>Evaluates the comparison on the words from <paramref name="top"/> down, in limb layout.</summary>
            static abstract bool Evaluate(ref ulong top);
        }

        /// <summary>ISZERO: whether the top word is zero.</summary>
        internal readonly struct IsZeroCondition : IStackCondition
        {
            public static int Inputs => 1;

            public static nint SharedHandler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<Math1Opcode<EvmInstructions.OpIsZero, OffFlag>, OffFlag, OffFlag, OnFlag>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong top) =>
                (top | Unsafe.Add(ref top, 1) | Unsafe.Add(ref top, 2) | Unsafe.Add(ref top, 3)) == 0;
        }

        /// <summary>EQ: whether the top two words are equal.</summary>
        internal readonly struct EqualCondition : IStackCondition
        {
            public static int Inputs => 2;

            public static nint SharedHandler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<BitwiseOpcode<EvmInstructions.OpBitwiseEq, OffFlag>, OffFlag, OffFlag, OnFlag>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong top)
            {
                ref ulong second = ref Unsafe.Subtract(ref top, EvmStack.WordSize / sizeof(ulong));
                return ((top ^ second) | (Unsafe.Add(ref top, 1) ^ Unsafe.Add(ref second, 1)) |
                    (Unsafe.Add(ref top, 2) ^ Unsafe.Add(ref second, 2)) | (Unsafe.Add(ref top, 3) ^ Unsafe.Add(ref second, 3))) == 0;
            }
        }

        /// <summary>LT: whether the top word is below the one under it, unsigned.</summary>
        internal readonly struct LessThanCondition : IStackCondition
        {
            public static int Inputs => 2;

            public static nint SharedHandler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<Math2Opcode<EvmInstructions.OpLt, OffFlag>, OffFlag, OffFlag, OnFlag>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong top) => IsBelow(ref top, ref Unsafe.Subtract(ref top, EvmStack.WordSize / sizeof(ulong)));
        }

        /// <summary>GT: whether the top word is above the one under it, unsigned.</summary>
        internal readonly struct GreaterThanCondition : IStackCondition
        {
            public static int Inputs => 2;

            public static nint SharedHandler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<Math2Opcode<EvmInstructions.OpGt, OffFlag>, OffFlag, OffFlag, OnFlag>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong top) => IsBelow(ref Unsafe.Subtract(ref top, EvmStack.WordSize / sizeof(ulong)), ref top);
        }

        /// <summary>
        /// DUP1, fused with a selector dispatch after it - <c>PUSH4</c> <c>EQ</c> <c>PUSH2</c> <c>JUMPI</c> - whenever that
        /// branch falls through or lands on a destination the incremental bitmap already holds.
        /// </summary>
        /// <remarks>
        /// A contract's dispatcher compares the selector against each function's in turn, so this sequence runs once per
        /// function it passes; fused, it compares the top word with the selector in place and leaves the stack as it
        /// found it. The fused step charges every opcode it covers; with too little gas for all of them, too little room
        /// for the two pushes, or a branch it cannot take in line, DUP1 runs alone. A short stack or gas, or a full one,
        /// runs the shared DUP1 handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteDup1(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
        {
            if (head > 0 && head < EvmStack.MaxStackSize - 1 && gas >= VeryLowGasCost.GasCost)
            {
                ref ulong top = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                // The ten bytes after DUP1: PUSH4, its four immediates, EQ, PUSH2, its two immediates and JUMPI.
                if (Unsafe.Add(ref code, pc + 1) == (byte)Instruction.PUSH4 &&
                    Unsafe.Add(ref code, pc + 6) == (byte)Instruction.EQ && head < EvmStack.MaxStackSize - 2)
                {
                    uint branch = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref code, pc + 7));
                    if ((byte)branch == (byte)Instruction.PUSH2 && branch >> 24 == (byte)Instruction.JUMPI)
                    {
                        ulong selector = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref code, pc + 2)));
                        if (((top ^ selector) | Unsafe.Add(ref top, 1) | Unsafe.Add(ref top, 2) | Unsafe.Add(ref top, 3)) != 0)
                        {
                            ulong notTakenGas = 4 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost;
                            if (gas >= notTakenGas)
                            {
                                gas -= notTakenGas;
                                pc += 11;
                                goto Dispatch;
                            }
                        }
                        else
                        {
                            ulong takenGas = 4 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                            nuint destination = BinaryPrimitives.ReverseEndianness((ushort)(branch >> 8));
                            if (gas >= takenGas && destination < (nuint)codeLength && stack.IsAnalyzedJumpDestination(destination))
                            {
                                gas -= takenGas;
                                pc = (nint)destination + 1;
                                goto Dispatch;
                            }
                        }
                    }
                }

                ref ulong copy = ref Unsafe.Add(ref top, EvmStack.WordSize / sizeof(ulong));
                copy = top;
                Unsafe.Add(ref copy, 1) = Unsafe.Add(ref top, 1);
                Unsafe.Add(ref copy, 2) = Unsafe.Add(ref top, 2);
                Unsafe.Add(ref copy, 3) = Unsafe.Add(ref top, 3);
                head++;
                gas -= VeryLowGasCost.GasCost;
                pc++;
                goto Dispatch;
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<DupOpcode<EvmInstructions.Op1, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);

        Dispatch:
            nint next = handlers[Unsafe.Add(ref code, pc)];
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
        }

        /// <summary>PUSH1 with its immediate and the opcode after it read from one address.</summary>
        /// <remarks>
        /// A short gas or a full stack runs the shared PUSH1 handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePush1(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
        {
            if (gas >= VeryLowGasCost.GasCost && head < EvmStack.MaxStackSize - 1)
            {
                ref byte immediate = ref Unsafe.Add(ref code, pc + 1);
                nint next = handlers[Unsafe.Add(ref immediate, 1)];
                SetWord(ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head)), immediate);
                gas -= VeryLowGasCost.GasCost;
                pc += 2;
                head++;
                return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<PushOpcode<EvmInstructions.Op1, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);
        }

        /// <summary>PUSH2, fused with a JUMP or JUMPI after it whenever <see cref="EvmInstructions.InstructionPush2{TGasPolicy, TTracingInst}"/> would fuse them.</summary>
        /// <remarks>
        /// Fuses on the same conditions: a destination the incremental bitmap already holds, or a zero JUMPI
        /// condition that never reads it. Every case that could fault - short gas, a full stack or a fused jump's
        /// condition missing - runs the shared PUSH2 handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePush2(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
        {
            ulong pushGas = VeryLowGasCost.GasCost;
            if (gas < pushGas || head >= EvmStack.MaxStackSize - 1)
                goto Shared;

            ref byte immediates = ref Unsafe.Add(ref code, pc + 1);
            nuint value = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref immediates));
            byte following = Unsafe.Add(ref immediates, 2);
            if (following == (byte)Instruction.JUMP)
            {
                if (value < (nuint)codeLength && stack.IsAnalyzedJumpDestination(value))
                {
                    ulong fusedGas = pushGas + JumpGasCost.GasCost + JumpDestGasCost.GasCost;
                    if (gas < fusedGas)
                        goto Shared;

                    gas -= fusedGas;
                    pc = (nint)value + 1;
                    goto Dispatch;
                }
            }
            else if (following == (byte)Instruction.JUMPI)
            {
                // The JUMPI would find no condition; the shared handler faults on it as the unfused pair does.
                if (head == 0)
                    goto Shared;

                ref ulong condition = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                if ((condition | Unsafe.Add(ref condition, 1) | Unsafe.Add(ref condition, 2) | Unsafe.Add(ref condition, 3)) == 0)
                {
                    ulong notTakenGas = pushGas + JumpIGasCost.GasCost;
                    if (gas < notTakenGas)
                        goto Shared;

                    head--;
                    gas -= notTakenGas;
                    pc += 4;
                    goto Dispatch;
                }

                if (value < (nuint)codeLength && stack.IsAnalyzedJumpDestination(value))
                {
                    ulong takenGas = pushGas + JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                    if (gas < takenGas)
                        goto Shared;

                    head--;
                    gas -= takenGas;
                    pc = (nint)value + 1;
                    goto Dispatch;
                }
            }

            SetWord(ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head)), value);
            gas -= pushGas;
            pc += 3;
            head++;
            // Read again rather than held: live through the JUMPI branch, the opcode takes a callee-saved register there.
            nint next = handlers[Unsafe.Add(ref code, pc)];
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);

        Dispatch:
            nint target = handlers[Unsafe.Add(ref code, pc)];
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, target);

        Shared:
            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<Push2Opcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);
        }

        /// <summary>MSTORE of a word that needs no clearing and no new backing, growing the active memory over it when it has to.</summary>
        /// <remarks>
        /// Such a word starts inside the initialized memory, so this covers memory growing a word at a time as well. Every
        /// other case - short gas or stack, a word that leaves a gap or outgrows the backing, an offset of 2^32 or more,
        /// which runs out of gas - runs the shared MSTORE handler instead.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteMStoreWithoutClearing(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
        {
            if (head > 1 && gas >= VeryLowGasCost.GasCost)
            {
                ref ulong offset = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                if ((Unsafe.Add(ref offset, 1) | Unsafe.Add(ref offset, 2) | Unsafe.Add(ref offset, 3) | (offset >> 32)) == 0)
                {
                    ulong remaining = gas - VeryLowGasCost.GasCost;
                    ref byte destination = ref state.Vm.VmState.Memory.TryPrepareWordOverwrite(offset, ref remaining);
                    if (!Unsafe.IsNullRef(ref destination))
                    {
                        // The value sits below the offset in limb layout; memory holds it big-endian.
                        ref ulong value = ref Unsafe.Subtract(ref offset, EvmStack.WordSize / sizeof(ulong));
                        Unsafe.WriteUnaligned(ref destination, BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref value, 3)));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8), BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref value, 2)));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref value, 1)));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 24), BinaryPrimitives.ReverseEndianness(value));
                        head -= 2;
                        gas = remaining;
                        pc++;
                        nint next = handlers[Unsafe.Add(ref code, pc)];
                        return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
                    }
                }
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<MStoreOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);
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
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
        {
            if (head > 0 && gas >= VeryLowGasCost.GasCost)
            {
                ref ulong slot = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                if ((Unsafe.Add(ref slot, 1) | Unsafe.Add(ref slot, 2) | Unsafe.Add(ref slot, 3) | (slot >> 32)) == 0)
                {
                    ref byte source = ref state.Vm.VmState.Memory.GetActiveInitializedWord(slot);
                    if (!Unsafe.IsNullRef(ref source))
                    {
                        // Memory holds the word big-endian; the slot takes it in limb layout, over the offset it held.
                        LoadBigEndian(ref slot, ref source);
                        gas -= VeryLowGasCost.GasCost;
                        pc++;
                        nint next = handlers[Unsafe.Add(ref code, pc)];
                        return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
                    }
                }
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<MLoadOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);
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
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
        {
            if (head > 0 && gas >= VeryLowGasCost.GasCost)
            {
                ref ulong slot = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                ulong offset = slot;
                ReadOnlySpan<byte> inputData = state.Vm.VmState.Env.InputData.Span;
                // Below 2^32 the offset cannot overflow when the word is added to it.
                if ((Unsafe.Add(ref slot, 1) | Unsafe.Add(ref slot, 2) | Unsafe.Add(ref slot, 3) | (offset >> 32)) == 0 &&
                    offset + EvmStack.WordSize <= (uint)inputData.Length)
                {
                    // The input holds the word big-endian; the slot takes it in limb layout, over the offset it held.
                    ref byte source = ref Unsafe.Add(ref MemoryMarshal.GetReference(inputData), (nint)offset);
                    LoadBigEndian(ref slot, ref source);
                    gas -= VeryLowGasCost.GasCost;
                    pc++;
                    nint next = handlers[Unsafe.Add(ref code, pc)];
                    return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
                }
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<CallDataLoadOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);
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
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
        {
            if (head > 1)
            {
                ref ulong offsetLimbs = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                ref ulong lengthLimbs = ref Unsafe.Subtract(ref offsetLimbs, EvmStack.WordSize / sizeof(ulong));
                ulong offset = offsetLimbs;
                ulong length = lengthLimbs;
                // Below 2^32 neither the word count nor the end of the range can overflow.
                if ((Unsafe.Add(ref offsetLimbs, 1) | Unsafe.Add(ref offsetLimbs, 2) | Unsafe.Add(ref offsetLimbs, 3) |
                     Unsafe.Add(ref lengthLimbs, 1) | Unsafe.Add(ref lengthLimbs, 2) | Unsafe.Add(ref lengthLimbs, 3) |
                     ((offset | length) >> 32)) == 0)
                {
                    ulong cost = GasCostOf.Sha3 + GasCostOf.Sha3Word * ((length + (EvmStack.WordSize - 1)) >> 5);
                    ref byte data = ref state.Vm.VmState.Memory.GetActiveInitializedRange(offset, length);
                    if (gas >= cost && !Unsafe.IsNullRef(ref data))
                    {
                        gas -= cost;
                        head--;
                        KeccakCache.ComputeTo(MemoryMarshal.CreateReadOnlySpan(ref data, (int)length), out Unsafe.As<ulong, ValueHash256>(ref lengthLimbs));
                        // Reloaded rather than held across the call, where each would take a callee-saved register.
                        handlers = state.OpcodeHandlers;
                        code = ref stack.Code;
                        codeLength = stack.CodeLength;
                        // The hash lands big-endian over the length; the slot holds it in limb layout.
                        ref ulong hash = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                        ulong limb3 = BinaryPrimitives.ReverseEndianness(hash);
                        ulong limb2 = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref hash, 1));
                        ulong limb1 = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref hash, 2));
                        ulong limb0 = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref hash, 3));
                        hash = limb0;
                        Unsafe.Add(ref hash, 1) = limb1;
                        Unsafe.Add(ref hash, 2) = limb2;
                        Unsafe.Add(ref hash, 3) = limb3;
                        pc++;
                        nint next = handlers[Unsafe.Add(ref code, pc)];
                        return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
                    }
                }
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<KeccakOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);
        }

        /// <summary>SHL with the shift in line; an amount of 256 or more clears the word.</summary>
        /// <remarks>A short stack or gas runs the shared SHL handler instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteShl(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
        {
            if (head > 1 && gas >= VeryLowGasCost.GasCost)
            {
                ref ulong shift = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                ref ulong value = ref Unsafe.Subtract(ref shift, EvmStack.WordSize / sizeof(ulong));
                ulong bits = shift;
                if ((Unsafe.Add(ref shift, 1) | Unsafe.Add(ref shift, 2) | Unsafe.Add(ref shift, 3) | (bits >> 8)) == 0)
                    ShiftLeft(ref value, (int)bits);
                else
                    SetWord(ref value, 0);

                head--;
                gas -= VeryLowGasCost.GasCost;
                pc++;
                nint next = handlers[Unsafe.Add(ref code, pc)];
                return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<ShiftOpcode<EvmInstructions.OpShl, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);
        }

        /// <summary>SHR with the shift in line; an amount of 256 or more clears the word.</summary>
        /// <remarks>A short stack or gas runs the shared SHR handler instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteShr(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
        {
            if (head > 1 && gas >= VeryLowGasCost.GasCost)
            {
                ref ulong shift = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                ref ulong value = ref Unsafe.Subtract(ref shift, EvmStack.WordSize / sizeof(ulong));
                ulong bits = shift;
                if ((Unsafe.Add(ref shift, 1) | Unsafe.Add(ref shift, 2) | Unsafe.Add(ref shift, 3) | (bits >> 8)) == 0)
                    ShiftRight(ref value, (int)bits);
                else
                    SetWord(ref value, 0);

                head--;
                gas -= VeryLowGasCost.GasCost;
                pc++;
                nint next = handlers[Unsafe.Add(ref code, pc)];
                return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<ShiftOpcode<EvmInstructions.OpShr, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);
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
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
        {
            ulong jumpAndJumpDestGas = JumpGasCost.GasCost + JumpDestGasCost.GasCost;
            if (head > 0 && gas >= jumpAndJumpDestGas)
            {
                ref ulong destination = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                nuint target = (nuint)destination;
                if ((Unsafe.Add(ref destination, 1) | Unsafe.Add(ref destination, 2) | Unsafe.Add(ref destination, 3)) == 0 &&
                    target < (nuint)codeLength)
                {
                    if (!stack.IsAnalyzedJumpDestination(target))
                    {
                        nint analyze = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                            &ExecuteJumpToUnanalyzedDestination<OffFlag>;
                        return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, analyze);
                    }

                    head--;
                    gas -= jumpAndJumpDestGas;
                    pc = (nint)target + 1;
                    nint next = handlers[Unsafe.Add(ref code, pc)];
                    return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
                }
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteOpcode<JumpOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);
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
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
        {
            ulong jumpIAndJumpDestGas = JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
            if (head > 1 && gas >= JumpIGasCost.GasCost)
            {
                ref ulong destination = ref Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
                ref ulong condition = ref Unsafe.Subtract(ref destination, EvmStack.WordSize / sizeof(ulong));
                if ((condition | Unsafe.Add(ref condition, 1) | Unsafe.Add(ref condition, 2) | Unsafe.Add(ref condition, 3)) == 0)
                {
                    head -= 2;
                    gas -= JumpIGasCost.GasCost;
                    pc++;
                    nint notTaken = handlers[Unsafe.Add(ref code, pc)];
                    return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, notTaken);
                }

                nuint target = (nuint)destination;
                if (gas >= jumpIAndJumpDestGas &&
                    (Unsafe.Add(ref destination, 1) | Unsafe.Add(ref destination, 2) | Unsafe.Add(ref destination, 3)) == 0 &&
                    target < (nuint)codeLength)
                {
                    if (!stack.IsAnalyzedJumpDestination(target))
                    {
                        nint analyze = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                            &ExecuteJumpToUnanalyzedDestination<OnFlag>;
                        return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, analyze);
                    }

                    head -= 2;
                    gas -= jumpIAndJumpDestGas;
                    pc = (nint)target + 1;
                    nint taken = handlers[Unsafe.Add(ref code, pc)];
                    return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, taken);
                }
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteJumpIfOpcode<OffFlag, OffFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);
        }

        /// <summary>
        /// The rest of <see cref="ExecuteJumpToAnalyzedDestination"/> or, with <typeparamref name="TConditional"/>,
        /// <see cref="ExecuteJumpIfToAnalyzedDestination"/>, for a taken jump whose destination the bitmap does not hold yet.
        /// </summary>
        /// <remarks>
        /// Entered only from there, with the stack, gas, condition and range checks passed. Most destinations are
        /// proven by the 32 bytes before them, which takes no call, so this handler has no frame either; the rest go to
        /// <see cref="ExecuteJumpToScannedDestination{TConditional}"/>.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteJumpToUnanalyzedDestination<TConditional>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
            where TConditional : struct, IFlag
        {
            nint target = (nint)Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
            Debug.Assert((nuint)target < (nuint)codeLength);
            if (stack.TryMarkJumpDestination(target))
            {
                head -= TConditional.IsActive ? 2 : 1;
                gas -= JumpAndJumpDestGas<TConditional>();
                pc = target + 1;
                nint next = handlers[Unsafe.Add(ref code, pc)];
                return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
            }

            nint scan = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                &ExecuteJumpToScannedDestination<TConditional>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, scan);
        }

        /// <summary>The rest of <see cref="ExecuteJumpToUnanalyzedDestination{TConditional}"/> for a destination a single look-back cannot decide.</summary>
        /// <remarks>
        /// Analyzing the destination needs a call, so this handler has a frame and the handlers before it do not pay
        /// for it. An invalid destination runs the shared jump handler, which faults on it.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteJumpToScannedDestination<TConditional>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
            where TConditional : struct, IFlag
        {
            int target = (int)Unsafe.As<byte, ulong>(ref stack.SlotUnchecked(head - 1));
            bool valid = stack.AnalyzeJumpDestination(target);
            // Reloaded rather than held across the call, where each would take a callee-saved register.
            handlers = state.OpcodeHandlers;
            code = ref stack.Code;
            codeLength = stack.CodeLength;
            if (valid)
            {
                head -= TConditional.IsActive ? 2 : 1;
                gas -= JumpAndJumpDestGas<TConditional>();
                pc = target + 1;
                nint next = handlers[Unsafe.Add(ref code, pc)];
                return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, next);
            }

            nint shared = TConditional.IsActive
                ? (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                    &ExecuteJumpIfOpcode<OffFlag, OffFlag>
                : (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)
                    &ExecuteOpcode<JumpOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, pc, head, handlers, ref code, codeLength, shared);
        }

        /// <summary>The charge of a taken JUMP, or with <typeparamref name="TConditional"/> a taken JUMPI, and the JUMPDEST it lands on.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong JumpAndJumpDestGas<TConditional>() where TConditional : struct, IFlag =>
            (TConditional.IsActive ? JumpIGasCost.GasCost : JumpGasCost.GasCost) + JumpDestGasCost.GasCost;
    }
}
