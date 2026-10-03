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
        lookup[(int)Instruction.ADD] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.AddOperation>);
        lookup[(int)Instruction.SUB] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.SubtractOperation>);
        lookup[(int)Instruction.AND] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.AndOperation>);
        lookup[(int)Instruction.OR] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.OrOperation>);
        lookup[(int)Instruction.XOR] = AsTableEntry(&RawCalliHelper.ExecuteBinary<RawCalliHelper.XorOperation>);
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
        }
    }

    private static partial class RawCalliHelper
    {
        private const int LimbsPerWord = EvmStack.WordSize / sizeof(ulong);

        /// <summary>Charges <paramref name="cost"/> to <paramref name="gas"/>, or reports that it does not cover it and leaves it unchanged.</summary>
        /// <remarks>
        /// Tests the sign of the charged gas, which is exact for carried gas of at most <see cref="long.MaxValue"/>: the
        /// charge doubles as the test, one instruction fewer than comparing first, and only the miss pays it back.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryCharge(ref ulong gas, ulong cost)
        {
            Debug.Assert(gas <= long.MaxValue, "The sign test is exact only for gas that fits a long.");
            gas -= cost;
            if ((long)gas >= 0) return true;
            gas += cost;
            return false;
        }

        /// <summary>The slot at <paramref name="index"/>, counted up from <paramref name="bottom"/>, for callers that have bounded the index.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte SlotAt(ref byte bottom, nint index) => ref Unsafe.Add(ref bottom, index * EvmStack.WordSize);

        /// <summary>The big-endian 16-bit value in the two lowest bytes of <paramref name="bytes"/>; the bytes above them are ignored.</summary>
        /// <remarks>The full-width byte reversal leaves it zero-extended by its shift, where a 16-bit one needs one more instruction to clear the bits above it.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint ReadBigEndianUInt16(ulong bytes) => (nuint)(BinaryPrimitives.ReverseEndianness(bytes) >> 48);

        /// <summary>Marks a direct tail transfer that the opcode weaver emits into the calling handler.</summary>
        /// <remarks>The target is last to match the calli evaluation stack; this method must never survive weaving.</remarks>
        private static EvmExceptionType TailDispatch(
            ref EvmStack stack, ulong gas, ref DispatchState state, ref byte ip, nint head,
            nint* handlers, ref byte code, ref byte bottom, nint target) =>
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
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
            where TCondition : struct, IStackCondition
        {
            if (head >= TCondition.Inputs && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                // Addressed off the head: keeps the handler frameless.
                ref ulong end = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head));
                ulong condition = TCondition.Evaluate(ref end) ? 1UL : 0UL;
                // The branch's PUSH2 overflows a stack the comparison leaves full, which only ISZERO can.
                if (TCondition.Inputs > 1 || head < EvmStack.MaxStackSize - 1)
                {
                    // PUSH2, its two immediates and JUMPI.
                    ref byte opcode = ref ip;
                    uint branch = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref opcode, 1));
                    if ((byte)branch == (byte)Instruction.PUSH2 && branch >> 24 == (byte)Instruction.JUMPI)
                    {
                        // The comparison is charged; the fused step charges the rest.
                        if (condition == 0)
                        {
                            ulong notTakenGas = VeryLowGasCost.GasCost + JumpIGasCost.GasCost;
                            if (gas >= notTakenGas)
                            {
                                head -= TCondition.Inputs;
                                gas -= notTakenGas;
                                ip = ref Unsafe.Add(ref ip, 5);
                                goto Dispatch;
                            }
                        }
                        else
                        {
                            ulong takenGas = VeryLowGasCost.GasCost + JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                            nuint destination = ReadBigEndianUInt16(branch >> 8);
                            if (gas >= takenGas && destination < (nuint)stack.CodeLength && stack.IsAnalyzedJumpDestination(destination))
                            {
                                head -= TCondition.Inputs;
                                gas -= takenGas;
                                ip = ref Unsafe.Add(ref code, (nint)destination + 1);
                                goto Dispatch;
                            }
                        }
                    }
                    // An ISZERO between the comparison and the branch only inverts what the branch tests.
                    else if ((byte)branch == (byte)Instruction.ISZERO)
                    {
                        branch = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref opcode, 2));
                        if ((byte)branch == (byte)Instruction.PUSH2 && branch >> 24 == (byte)Instruction.JUMPI)
                        {
                            if (condition != 0)
                            {
                                ulong notTakenGas = 2 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost;
                                if (gas >= notTakenGas)
                                {
                                    head -= TCondition.Inputs;
                                    gas -= notTakenGas;
                                    ip = ref Unsafe.Add(ref ip, 6);
                                    goto Dispatch;
                                }
                            }
                            else
                            {
                                ulong takenGas = 2 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                                nuint destination = ReadBigEndianUInt16(branch >> 8);
                                if (gas >= takenGas && destination < (nuint)stack.CodeLength && stack.IsAnalyzedJumpDestination(destination))
                                {
                                    head -= TCondition.Inputs;
                                    gas -= takenGas;
                                    ip = ref Unsafe.Add(ref code, (nint)destination + 1);
                                    goto Dispatch;
                                }
                            }
                        }
                    }
                }

                // Unfused: the result replaces the deepest input, in limb layout.
                nint result = -TCondition.Inputs * LimbsPerWord;
                Unsafe.Add(ref end, result) = condition;
                Unsafe.Add(ref end, result + 1) = 0;
                Unsafe.Add(ref end, result + 2) = 0;
                Unsafe.Add(ref end, result + 3) = 0;
                head -= TCondition.Inputs - 1;
                ip = ref Unsafe.Add(ref ip, 1);
                goto Dispatch;
            }

            nint shared = TCondition.SharedHandler;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);

        Dispatch:
            nint next = handlers[ip];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
        }

        /// <summary>A comparison of the words on top of the stack, as <see cref="ExecuteCondition{TCondition}"/> runs it.</summary>
        internal interface IStackCondition
        {
            /// <summary>How many words the comparison consumes.</summary>
            static abstract int Inputs { get; }

            /// <summary>The shared handler of the comparison, which handles every case that faults.</summary>
            static abstract nint SharedHandler { get; }

            /// <summary>Evaluates the comparison on the words below <paramref name="end"/>, the limb above the top word.</summary>
            /// <remarks>Every limb is addressed off <paramref name="end"/> itself, so each access folds its constant into its own offset.</remarks>
            static abstract bool Evaluate(ref ulong end);
        }

        /// <summary>ISZERO: whether the top word is zero.</summary>
        internal readonly struct IsZeroCondition : IStackCondition
        {
            public static int Inputs => 1;

            public static nint SharedHandler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<Math1Opcode<EvmInstructions.OpIsZero, OffFlag>, OffFlag, OffFlag, OnFlag>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong end) =>
                (Unsafe.Add(ref end, -4) | Unsafe.Add(ref end, -3) | Unsafe.Add(ref end, -2) | Unsafe.Add(ref end, -1)) == 0;
        }

        /// <summary>EQ: whether the top two words are equal.</summary>
        internal readonly struct EqualCondition : IStackCondition
        {
            public static int Inputs => 2;

            public static nint SharedHandler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<BitwiseOpcode<EvmInstructions.OpBitwiseEq, OffFlag>, OffFlag, OffFlag, OnFlag>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong end) =>
                ((Unsafe.Add(ref end, -4) ^ Unsafe.Add(ref end, -8)) | (Unsafe.Add(ref end, -3) ^ Unsafe.Add(ref end, -7)) |
                    (Unsafe.Add(ref end, -2) ^ Unsafe.Add(ref end, -6)) | (Unsafe.Add(ref end, -1) ^ Unsafe.Add(ref end, -5))) == 0;
        }

        /// <summary>LT: whether the top word is below the one under it, unsigned.</summary>
        internal readonly struct LessThanCondition : IStackCondition
        {
            public static int Inputs => 2;

            public static nint SharedHandler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<Math2Opcode<EvmInstructions.OpLt, OffFlag>, OffFlag, OffFlag, OnFlag>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong end) => IsBelow(ref end, -LimbsPerWord, -2 * LimbsPerWord);
        }

        /// <summary>GT: whether the top word is above the one under it, unsigned.</summary>
        internal readonly struct GreaterThanCondition : IStackCondition
        {
            public static int Inputs => 2;

            public static nint SharedHandler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<Math2Opcode<EvmInstructions.OpGt, OffFlag>, OffFlag, OffFlag, OnFlag>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref ulong end) => IsBelow(ref end, -2 * LimbsPerWord, -LimbsPerWord);
        }

        /// <summary>A very-low-cost operation on the top two words, with both addressed off the head.</summary>
        /// <remarks>A short stack or gas runs the shared handler of the operation instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteBinary<TOperation>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
            where TOperation : struct, IStackBinaryOperation
        {
            if (head > 1 && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                TOperation.Apply(ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head)));
                head--;
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = handlers[ip];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
            }

            nint shared = TOperation.SharedHandler;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
        }

        /// <summary>An operation on the top two words, as <see cref="ExecuteBinary{TOperation}"/> runs it.</summary>
        internal interface IStackBinaryOperation
        {
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
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<Math2Opcode<EvmInstructions.OpAdd, OffFlag>, OffFlag, OffFlag, OnFlag>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                ulong left = Unsafe.Add(ref end, -8);
                ulong sum = left + Unsafe.Add(ref end, -4);
                ulong carry = sum < left ? 1UL : 0UL;
                Unsafe.Add(ref end, -8) = sum;
                carry = AddLimb(ref end, -7, carry);
                carry = AddLimb(ref end, -6, carry);
                Unsafe.Add(ref end, -5) = Unsafe.Add(ref end, -5) + Unsafe.Add(ref end, -1) + carry;
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
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<Math2Opcode<EvmInstructions.OpSub, OffFlag>, OffFlag, OffFlag, OnFlag>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                ulong minuend = Unsafe.Add(ref end, -4);
                ulong subtrahend = Unsafe.Add(ref end, -8);
                ulong borrow = minuend < subtrahend ? 1UL : 0UL;
                Unsafe.Add(ref end, -8) = minuend - subtrahend;
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
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<BitwiseOpcode<EvmInstructions.OpBitwiseAnd, OffFlag>, OffFlag, OffFlag, OnFlag>;

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
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<BitwiseOpcode<EvmInstructions.OpBitwiseOr, OffFlag>, OffFlag, OffFlag, OnFlag>;

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
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<BitwiseOpcode<EvmInstructions.OpBitwiseXor, OffFlag>, OffFlag, OffFlag, OnFlag>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                Unsafe.Add(ref end, -8) = Unsafe.Add(ref end, -8) ^ Unsafe.Add(ref end, -4);
                Unsafe.Add(ref end, -7) = Unsafe.Add(ref end, -7) ^ Unsafe.Add(ref end, -3);
                Unsafe.Add(ref end, -6) = Unsafe.Add(ref end, -6) ^ Unsafe.Add(ref end, -2);
                Unsafe.Add(ref end, -5) = Unsafe.Add(ref end, -5) ^ Unsafe.Add(ref end, -1);
            }
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
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            if (head > 0 && head < EvmStack.MaxStackSize - 1 && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong top = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
                // PUSH4, its four immediates, EQ, PUSH2, its two immediates and JUMPI.
                ref byte opcode = ref ip;
                if (Unsafe.Add(ref opcode, 1) == (byte)Instruction.PUSH4 &&
                    Unsafe.Add(ref opcode, 6) == (byte)Instruction.EQ && head < EvmStack.MaxStackSize - 2)
                {
                    uint branch = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref opcode, 7));
                    if ((byte)branch == (byte)Instruction.PUSH2 && branch >> 24 == (byte)Instruction.JUMPI)
                    {
                        ulong selector = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref opcode, 2)));
                        // DUP1 is charged; the fused step charges the rest.
                        if (((top ^ selector) | Unsafe.Add(ref top, 1) | Unsafe.Add(ref top, 2) | Unsafe.Add(ref top, 3)) != 0)
                        {
                            ulong notTakenGas = 3 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost;
                            if (gas >= notTakenGas)
                            {
                                gas -= notTakenGas;
                                ip = ref Unsafe.Add(ref ip, 11);
                                goto Dispatch;
                            }
                        }
                        else
                        {
                            ulong takenGas = 3 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                            nuint destination = ReadBigEndianUInt16(branch >> 8);
                            if (gas >= takenGas && destination < (nuint)stack.CodeLength && stack.IsAnalyzedJumpDestination(destination))
                            {
                                gas -= takenGas;
                                ip = ref Unsafe.Add(ref code, (nint)destination + 1);
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
                ip = ref Unsafe.Add(ref ip, 1);
                goto Dispatch;
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<DupOpcode<EvmInstructions.Op1, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);

        Dispatch:
            nint next = handlers[ip];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
        }

        /// <summary>DUPn, for every n but 1, with both words addressed off the head.</summary>
        /// <remarks>A short stack or gas, or a full one, runs the shared DUPn handler instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteDup<TOpCount>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
            where TOpCount : struct, EvmInstructions.IOpCount
        {
            // One unsigned test bounds the depth on both sides: below the source the difference wraps past the limit.
            if ((nuint)(head - TOpCount.Count) < (nuint)(EvmStack.MaxStackSize - 1 - TOpCount.Count) && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                // The source addressed off the copy itself, so each access folds its constant into its own offset.
                ref ulong copy = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head));
                nint source = -TOpCount.Count * LimbsPerWord;
                copy = Unsafe.Add(ref copy, source);
                Unsafe.Add(ref copy, 1) = Unsafe.Add(ref copy, source + 1);
                Unsafe.Add(ref copy, 2) = Unsafe.Add(ref copy, source + 2);
                Unsafe.Add(ref copy, 3) = Unsafe.Add(ref copy, source + 3);
                head++;
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = handlers[ip];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<DupOpcode<TOpCount, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
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
            ulong gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
            where TOpCount : struct, EvmInstructions.IOpCount
        {
            if (head > TOpCount.Count && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong end = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head));
                SwapLimbs(ref end, -LimbsPerWord, -(TOpCount.Count + 1) * LimbsPerWord);
                SwapLimbs(ref end, 1 - LimbsPerWord, 1 - (TOpCount.Count + 1) * LimbsPerWord);
                SwapLimbs(ref end, 2 - LimbsPerWord, 2 - (TOpCount.Count + 1) * LimbsPerWord);
                SwapLimbs(ref end, 3 - LimbsPerWord, 3 - (TOpCount.Count + 1) * LimbsPerWord);
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = handlers[ip];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<SwapOpcode<TOpCount, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
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
            ulong gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            if (head < EvmStack.MaxStackSize - 1 && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                // Both read off the opcode's address, so the immediate's needs no add of its own.
                ref byte opcode = ref ip;
                nint next = handlers[Unsafe.Add(ref opcode, 2)];
                SetWord(ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head)), Unsafe.Add(ref opcode, 1));
                ip = ref Unsafe.Add(ref ip, 2);
                head++;
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<PushOpcode<EvmInstructions.Op1, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
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
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            ulong pushGas = VeryLowGasCost.GasCost;
            if (head >= EvmStack.MaxStackSize - 1 || !TryCharge(ref gas, pushGas))
                goto Shared;

            // The push is charged; a fused step charges the rest, and a miss pays the push back.
            ref byte opcode = ref ip;
            nuint value = ReadBigEndianUInt16(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref opcode, 1)));
            byte following = Unsafe.Add(ref opcode, 3);
            if (following == (byte)Instruction.JUMP)
            {
                if (value < (nuint)stack.CodeLength && stack.IsAnalyzedJumpDestination(value))
                {
                    ulong fusedGas = JumpGasCost.GasCost + JumpDestGasCost.GasCost;
                    if (gas < fusedGas)
                        goto Refund;

                    gas -= fusedGas;
                    ip = ref Unsafe.Add(ref code, (nint)value + 1);
                    goto Dispatch;
                }
            }
            else if (following == (byte)Instruction.JUMPI)
            {
                // The JUMPI would find no condition; the shared handler faults on it as the unfused pair does.
                if (head == 0)
                    goto Refund;

                ref ulong condition = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
                if ((condition | Unsafe.Add(ref condition, 1) | Unsafe.Add(ref condition, 2) | Unsafe.Add(ref condition, 3)) == 0)
                {
                    ulong notTakenGas = JumpIGasCost.GasCost;
                    if (gas < notTakenGas)
                        goto Refund;

                    head--;
                    gas -= notTakenGas;
                    ip = ref Unsafe.Add(ref ip, 4);
                    goto Dispatch;
                }

                if (value < (nuint)stack.CodeLength && stack.IsAnalyzedJumpDestination(value))
                {
                    ulong takenGas = JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
                    if (gas < takenGas)
                        goto Refund;

                    head--;
                    gas -= takenGas;
                    ip = ref Unsafe.Add(ref code, (nint)value + 1);
                    goto Dispatch;
                }
            }

            SetWord(ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head)), value);
            ip = ref Unsafe.Add(ref ip, 3);
            head++;
            // Read again rather than held: live through the JUMPI branch, the opcode takes a callee-saved register there.
            nint next = handlers[ip];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);

        Dispatch:
            nint target = handlers[ip];
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, target);

        Refund:
            gas += pushGas;
        Shared:
            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<Push2Opcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
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
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            if (head > 1 && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong offset = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
                if ((Unsafe.Add(ref offset, 1) | Unsafe.Add(ref offset, 2) | Unsafe.Add(ref offset, 3) | (offset >> 32)) == 0)
                {
                    // Charged in the carried gas: a separate copy would take a callee-saved register.
                    ref byte destination = ref state.Memory.TryPrepareWordOverwrite(offset, ref gas);
                    if (!Unsafe.IsNullRef(ref destination))
                    {
                        // The value is the word below the offset; memory holds it big-endian.
                        Unsafe.WriteUnaligned(ref destination, BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref offset, -1)));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8), BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref offset, -2)));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref offset, -3)));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 24), BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref offset, -4)));
                        head -= 2;
                        ip = ref Unsafe.Add(ref ip, 1);
                        nint next = handlers[ip];
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
                    }
                }

                gas += VeryLowGasCost.GasCost;
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<MStoreOpcode<OffFlag, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
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
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            if (head > 0 && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong slot = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
                if ((Unsafe.Add(ref slot, 1) | Unsafe.Add(ref slot, 2) | Unsafe.Add(ref slot, 3) | (slot >> 32)) == 0)
                {
                    ref byte source = ref state.Memory.GetActiveInitializedWord(slot);
                    if (!Unsafe.IsNullRef(ref source))
                    {
                        // Memory holds the word big-endian; the slot takes it in limb layout, over the offset it held.
                        LoadBigEndian(ref slot, ref source);
                        ip = ref Unsafe.Add(ref ip, 1);
                        nint next = handlers[ip];
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
                    }
                }

                gas += VeryLowGasCost.GasCost;
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<MLoadOpcode<OffFlag, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
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
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            if (head > 0 && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong slot = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
                ulong offset = slot;
                // Below 2^32 the offset cannot overflow when the word is added to it.
                if ((Unsafe.Add(ref slot, 1) | Unsafe.Add(ref slot, 2) | Unsafe.Add(ref slot, 3) | (offset >> 32)) == 0 &&
                    offset + EvmStack.WordSize <= (ulong)stack.InputDataLength)
                {
                    // The input holds the word big-endian; the slot takes it in limb layout, over the offset it held.
                    ref byte source = ref Unsafe.Add(ref Unsafe.AsRef(in stack.InputData), (nint)offset);
                    LoadBigEndian(ref slot, ref source);
                    ip = ref Unsafe.Add(ref ip, 1);
                    nint next = handlers[ip];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
                }

                gas += VeryLowGasCost.GasCost;
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<CallDataLoadOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
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
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            if (head > 1)
            {
                ref ulong offsetLimbs = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
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
                    if (gas >= cost && !Unsafe.IsNullRef(ref data))
                    {
                        gas -= cost;
                        head--;
                        KeccakCache.ComputeTo(MemoryMarshal.CreateReadOnlySpan(ref data, (int)length), out Unsafe.As<ulong, ValueHash256>(ref lengthLimbs));
                        // Reloaded rather than held across the call, where each would take a callee-saved register.
                        handlers = state.OpcodeHandlers;
                        code = ref stack.Code;
                        bottom = ref stack.Bottom;
                        // The hash lands big-endian over the length; the slot holds it in limb layout.
                        ref ulong hash = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
                        ulong limb3 = BinaryPrimitives.ReverseEndianness(hash);
                        ulong limb2 = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref hash, 1));
                        ulong limb1 = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref hash, 2));
                        ulong limb0 = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref hash, 3));
                        hash = limb0;
                        Unsafe.Add(ref hash, 1) = limb1;
                        Unsafe.Add(ref hash, 2) = limb2;
                        Unsafe.Add(ref hash, 3) = limb3;
                        ip = ref Unsafe.Add(ref ip, 1);
                        nint next = handlers[ip];
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
                    }
                }
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<KeccakOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
        }

        /// <summary>SHL with the shift in line; an amount of 256 or more clears the word.</summary>
        /// <remarks>A short stack or gas runs the shared SHL handler instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteShl(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            if (head > 1 && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong shift = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
                ref ulong value = ref Unsafe.Subtract(ref shift, EvmStack.WordSize / sizeof(ulong));
                ulong bits = shift;
                if ((Unsafe.Add(ref shift, 1) | Unsafe.Add(ref shift, 2) | Unsafe.Add(ref shift, 3) | (bits >> 8)) == 0)
                    ShiftLeft(ref value, (int)bits);
                else
                    SetWord(ref value, 0);

                head--;
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = handlers[ip];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<ShiftOpcode<EvmInstructions.OpShl, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
        }

        /// <summary>SHR with the shift in line; an amount of 256 or more clears the word.</summary>
        /// <remarks>A short stack or gas runs the shared SHR handler instead, which faults on it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteShr(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            if (head > 1 && TryCharge(ref gas, VeryLowGasCost.GasCost))
            {
                ref ulong shift = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
                ref ulong value = ref Unsafe.Subtract(ref shift, EvmStack.WordSize / sizeof(ulong));
                ulong bits = shift;
                if ((Unsafe.Add(ref shift, 1) | Unsafe.Add(ref shift, 2) | Unsafe.Add(ref shift, 3) | (bits >> 8)) == 0)
                    ShiftRight(ref value, (int)bits);
                else
                    SetWord(ref value, 0);

                head--;
                ip = ref Unsafe.Add(ref ip, 1);
                nint next = handlers[ip];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<ShiftOpcode<EvmInstructions.OpShr, OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
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
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            ulong jumpAndJumpDestGas = JumpGasCost.GasCost + JumpDestGasCost.GasCost;
            if (head > 0 && TryCharge(ref gas, jumpAndJumpDestGas))
            {
                ref ulong destination = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
                nuint target = (nuint)destination;
                if ((Unsafe.Add(ref destination, 1) | Unsafe.Add(ref destination, 2) | Unsafe.Add(ref destination, 3)) == 0 &&
                    target < (nuint)stack.CodeLength)
                {
                    if (!stack.IsAnalyzedJumpDestination(target))
                    {
                        gas += jumpAndJumpDestGas;
                        nint analyze = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                            &ExecuteJumpToUnanalyzedDestination<OffFlag>;
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, analyze);
                    }

                    head--;
                    ip = ref Unsafe.Add(ref code, (nint)target + 1);
                    nint next = handlers[ip];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
                }

                gas += jumpAndJumpDestGas;
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteOpcode<JumpOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
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
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            ulong jumpIAndJumpDestGas = JumpIGasCost.GasCost + JumpDestGasCost.GasCost;
            if (head > 1 && gas >= JumpIGasCost.GasCost)
            {
                ref ulong destination = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
                ref ulong condition = ref Unsafe.Subtract(ref destination, EvmStack.WordSize / sizeof(ulong));
                if ((condition | Unsafe.Add(ref condition, 1) | Unsafe.Add(ref condition, 2) | Unsafe.Add(ref condition, 3)) == 0)
                {
                    head -= 2;
                    gas -= JumpIGasCost.GasCost;
                    ip = ref Unsafe.Add(ref ip, 1);
                    nint notTaken = handlers[ip];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, notTaken);
                }

                nuint target = (nuint)destination;
                if (gas >= jumpIAndJumpDestGas &&
                    (Unsafe.Add(ref destination, 1) | Unsafe.Add(ref destination, 2) | Unsafe.Add(ref destination, 3)) == 0 &&
                    target < (nuint)stack.CodeLength)
                {
                    if (!stack.IsAnalyzedJumpDestination(target))
                    {
                        nint analyze = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                            &ExecuteJumpToUnanalyzedDestination<OnFlag>;
                        return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, analyze);
                    }

                    head -= 2;
                    gas -= jumpIAndJumpDestGas;
                    ip = ref Unsafe.Add(ref code, (nint)target + 1);
                    nint taken = handlers[ip];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, taken);
                }
            }

            nint shared = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteJumpIfOpcode<OffFlag, OffFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
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
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
            where TConditional : struct, IFlag
        {
            nint target = (nint)Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
            Debug.Assert((nuint)target < (nuint)stack.CodeLength);
            if (stack.TryMarkJumpDestination(target))
            {
                head -= TConditional.IsActive ? 2 : 1;
                gas -= JumpAndJumpDestGas<TConditional>();
                ip = ref Unsafe.Add(ref code, target + 1);
                nint next = handlers[ip];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
            }

            nint scan = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                &ExecuteJumpToScannedDestination<TConditional>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, scan);
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
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
            where TConditional : struct, IFlag
        {
            int target = (int)Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
            bool valid = stack.AnalyzeJumpDestination(target);
            // Reloaded rather than held across the call, where each would take a callee-saved register.
            handlers = state.OpcodeHandlers;
            code = ref stack.Code;
            bottom = ref stack.Bottom;
            if (valid)
            {
                head -= TConditional.IsActive ? 2 : 1;
                gas -= JumpAndJumpDestGas<TConditional>();
                ip = ref Unsafe.Add(ref code, target + 1);
                nint next = handlers[ip];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
            }

            nint shared = TConditional.IsActive
                ? (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                    &ExecuteJumpIfOpcode<OffFlag, OffFlag>
                : (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                    &ExecuteOpcode<JumpOpcode<OffFlag>, OffFlag, OffFlag, OnFlag>;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, shared);
        }

        /// <summary>The charge of a taken JUMP, or with <typeparamref name="TConditional"/> a taken JUMPI, and the JUMPDEST it lands on.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong JumpAndJumpDestGas<TConditional>() where TConditional : struct, IFlag =>
            (TConditional.IsActive ? JumpIGasCost.GasCost : JumpGasCost.GasCost) + JumpDestGasCost.GasCost;
    }
}
