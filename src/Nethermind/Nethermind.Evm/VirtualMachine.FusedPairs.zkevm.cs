// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    /// <summary>The number of entries in a table the guest dispatches through, one per opcode and the byte after it.</summary>
    internal const int PairedHandlersLength = 1 << 16;

    /// <summary>The table <see cref="GetPairedHandlers"/> paired last, with the one it paired.</summary>
    private static PairedHandlers? _pairedHandlers;

    private sealed class PairedHandlers(object source, nint[] entries)
    {
        public readonly object Source = source;
        public readonly nint[] Entries = entries;
    }

    /// <summary>The table <see cref="PairHandlers"/> builds from <paramref name="handlers"/>, built again only for another table.</summary>
    private static nint[] GetPairedHandlers(delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[] handlers)
    {
        PairedHandlers? paired = Volatile.Read(ref _pairedHandlers);
        if (paired is null || paired.Source != handlers)
        {
            fixed (void* entries = handlers)
                paired = new PairedHandlers(handlers, PairHandlers(new ReadOnlySpan<nint>(entries, byte.MaxValue + 1)));
            Volatile.Write(ref _pairedHandlers, paired);
        }

        return paired.Entries;
    }

    /// <summary>
    /// Expands a 256-entry table into the one the guest dispatches through, indexed by an opcode and the byte after it,
    /// read together as one little-endian <see cref="ushort"/>.
    /// </summary>
    /// <remarks>
    /// Each entry holds the opcode's handler from <paramref name="handlers"/>, unless the two bytes are a pair of opcodes
    /// that a fused handler runs as one step and both opcodes have the guest handlers the fused one stands in for. The
    /// table picks the fused handler at no cost, where testing the next opcode in the first one's handler would cost
    /// every occurrence of it. A fused handler is entered only at the first opcode, so a jump onto the second still runs
    /// it alone; the byte after a PUSH is its immediate, so no pair starts with one.
    /// </remarks>
    internal static nint[] PairHandlers(ReadOnlySpan<nint> handlers)
    {
        nint[] paired = GC.AllocateUninitializedArray<nint>(PairedHandlersLength);
        for (int block = 0; block < PairedHandlersLength; block += byte.MaxValue + 1)
            handlers.CopyTo(paired.AsSpan(block));

        RawCalliHelper.ConfigurePairs(handlers, paired);
        return paired;
    }

    private static partial class RawCalliHelper
    {
        /// <summary>The index of the handler for the opcode at <paramref name="ip"/> in a table <see cref="PairHandlers"/> built.</summary>
        /// <remarks>
        /// ZisK charges a two-byte read about three times a one-byte one, so handlers whose next opcode seldom starts a
        /// fused pair index by the opcode alone instead: that is the entry for the opcode followed by STOP, which no pair
        /// ends in, so it holds the opcode's own handler.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nint PairAt(ref byte ip) => Unsafe.ReadUnaligned<ushort>(ref ip);

        /// <summary>Installs the fused handlers of the pairs that run often enough in mainnet blocks to pay for one.</summary>
        internal static void ConfigurePairs(ReadOnlySpan<nint> handlers, nint[] paired)
        {
            Fuse<BinaryStep<AddOperation>, SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.ADD, Instruction.SWAP1);
            Fuse<SwapStep<EvmInstructions.Op1>, DupStep<EvmInstructions.Op2>>(handlers, paired, Instruction.SWAP1, Instruction.DUP2);
            Fuse<DupStep<EvmInstructions.Op3>, BinaryStep<AddOperation>>(handlers, paired, Instruction.DUP3, Instruction.ADD);
            Fuse<SwapStep<EvmInstructions.Op1>, PopStep>(handlers, paired, Instruction.SWAP1, Instruction.POP);
            Fuse<SwapStep<EvmInstructions.Op2>, SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.SWAP2, Instruction.SWAP1);
            Fuse<SwapStep<EvmInstructions.Op2>, PopStep>(handlers, paired, Instruction.SWAP2, Instruction.POP);
            Fuse<DupStep<EvmInstructions.Op2>, BinaryStep<AddOperation>>(handlers, paired, Instruction.DUP2, Instruction.ADD);
            Fuse<DupStep<EvmInstructions.Op2>, DupStep<EvmInstructions.Op2>>(handlers, paired, Instruction.DUP2, Instruction.DUP2);
            Fuse<DupStep<EvmInstructions.Op3>, DupStep<EvmInstructions.Op2>>(handlers, paired, Instruction.DUP3, Instruction.DUP2);
            Fuse<DupStep<EvmInstructions.Op2>, SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.DUP2, Instruction.SWAP1);
            Fuse<BinaryStep<AddOperation>, SwapStep<EvmInstructions.Op3>>(handlers, paired, Instruction.ADD, Instruction.SWAP3);
            Fuse<SwapStep<EvmInstructions.Op1>, DupStep<EvmInstructions.Op1>>(handlers, paired, Instruction.SWAP1, Instruction.DUP1);
            Fuse<SwapStep<EvmInstructions.Op1>, SwapStep<EvmInstructions.Op4>>(handlers, paired, Instruction.SWAP1, Instruction.SWAP4);
            Fuse<SwapStep<EvmInstructions.Op4>, SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.SWAP4, Instruction.SWAP1);
            Fuse<SwapStep<EvmInstructions.Op4>, BinaryStep<AddOperation>>(handlers, paired, Instruction.SWAP4, Instruction.ADD);
            Fuse<DupStep<EvmInstructions.Op4>, BinaryStep<AddOperation>>(handlers, paired, Instruction.DUP4, Instruction.ADD);
            Fuse<SwapStep<EvmInstructions.Op1>, SwapStep<EvmInstructions.Op2>>(handlers, paired, Instruction.SWAP1, Instruction.SWAP2);
            Fuse<SwapStep<EvmInstructions.Op3>, SwapStep<EvmInstructions.Op2>>(handlers, paired, Instruction.SWAP3, Instruction.SWAP2);
            Fuse<SwapStep<EvmInstructions.Op3>, PopStep>(handlers, paired, Instruction.SWAP3, Instruction.POP);
            Fuse<BinaryStep<AndOperation>, DupStep<EvmInstructions.Op2>>(handlers, paired, Instruction.AND, Instruction.DUP2);
            Fuse<BinaryStep<AndOperation>, SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.AND, Instruction.SWAP1);
            Fuse<DupStep<EvmInstructions.Op2>, BinaryStep<AndOperation>>(handlers, paired, Instruction.DUP2, Instruction.AND);
            Fuse<DupStep<EvmInstructions.Op4>, DupStep<EvmInstructions.Op2>>(handlers, paired, Instruction.DUP4, Instruction.DUP2);
            Fuse<BinaryStep<AddOperation>, SwapStep<EvmInstructions.Op2>>(handlers, paired, Instruction.ADD, Instruction.SWAP2);
            Fuse<DupStep<EvmInstructions.Op5>, BinaryStep<AddOperation>>(handlers, paired, Instruction.DUP5, Instruction.ADD);
            Fuse<BinaryStep<SubtractOperation>, DupStep<EvmInstructions.Op2>>(handlers, paired, Instruction.SUB, Instruction.DUP2);
            Fuse<BinaryStep<AddOperation>, DupStep<EvmInstructions.Op1>>(handlers, paired, Instruction.ADD, Instruction.DUP1);
            Fuse<SwapStep<EvmInstructions.Op2>, BinaryStep<AddOperation>>(handlers, paired, Instruction.SWAP2, Instruction.ADD);
            Fuse<BinaryStep<AddOperation>, BinaryStep<AddOperation>>(handlers, paired, Instruction.ADD, Instruction.ADD);
            Fuse<BinaryStep<SubtractOperation>, BinaryStep<AndOperation>>(handlers, paired, Instruction.SUB, Instruction.AND);
            Fuse<SwapStep<EvmInstructions.Op2>, DupStep<EvmInstructions.Op3>>(handlers, paired, Instruction.SWAP2, Instruction.DUP3);
            Fuse<BinaryStep<SubtractOperation>, SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.SUB, Instruction.SWAP1);
            Fuse<PopStep, SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.POP, Instruction.SWAP1);
            Fuse<DupStep<EvmInstructions.Op3>, BinaryStep<AndOperation>>(handlers, paired, Instruction.DUP3, Instruction.AND);
            Fuse<DupStep<EvmInstructions.Op6>, BinaryStep<AddOperation>>(handlers, paired, Instruction.DUP6, Instruction.ADD);
            Fuse<PopStep, DupStep<EvmInstructions.Op4>>(handlers, paired, Instruction.POP, Instruction.DUP4);
            Fuse<DupStep<EvmInstructions.Op3>, DupStep<EvmInstructions.Op3>>(handlers, paired, Instruction.DUP3, Instruction.DUP3);
            Fuse<SwapStep<EvmInstructions.Op4>, SwapStep<EvmInstructions.Op3>>(handlers, paired, Instruction.SWAP4, Instruction.SWAP3);
            Fuse<DupStep<EvmInstructions.Op7>, BinaryStep<AddOperation>>(handlers, paired, Instruction.DUP7, Instruction.ADD);
            Fuse<PopStep, SwapStep<EvmInstructions.Op3>>(handlers, paired, Instruction.POP, Instruction.SWAP3);
            Fuse<DupStep<EvmInstructions.Op3>, DupStep<EvmInstructions.Op5>>(handlers, paired, Instruction.DUP3, Instruction.DUP5);
            Fuse<SwapStep<EvmInstructions.Op3>, SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.SWAP3, Instruction.SWAP1);
            Fuse<DupStep<EvmInstructions.Op3>, DupStep<EvmInstructions.Op9>>(handlers, paired, Instruction.DUP3, Instruction.DUP9);
            Fuse<DupStep<EvmInstructions.Op4>, DupStep<EvmInstructions.Op3>>(handlers, paired, Instruction.DUP4, Instruction.DUP3);
            Fuse<SwapStep<EvmInstructions.Op1>, DupStep<EvmInstructions.Op3>>(handlers, paired, Instruction.SWAP1, Instruction.DUP3);
            Fuse<BinaryStep<AddOperation>, DupStep<EvmInstructions.Op2>>(handlers, paired, Instruction.ADD, Instruction.DUP2);
            Fuse<BinaryStep<SubtractOperation>, DupStep<EvmInstructions.Op3>>(handlers, paired, Instruction.SUB, Instruction.DUP3);
            Fuse<PopStep, DupStep<EvmInstructions.Op1>>(handlers, paired, Instruction.POP, Instruction.DUP1);
            Fuse<BinaryStep<AddOperation>, BinaryStep<AndOperation>>(handlers, paired, Instruction.ADD, Instruction.AND);
            Fuse<DupStep<EvmInstructions.Op2>, DupStep<EvmInstructions.Op4>>(handlers, paired, Instruction.DUP2, Instruction.DUP4);
            Fuse<SwapStep<EvmInstructions.Op1>, SwapStep<EvmInstructions.Op3>>(handlers, paired, Instruction.SWAP1, Instruction.SWAP3);
            Fuse<BinaryStep<AndOperation>, DupStep<EvmInstructions.Op3>>(handlers, paired, Instruction.AND, Instruction.DUP3);
            Fuse<SwapStep<EvmInstructions.Op5>, PopStep>(handlers, paired, Instruction.SWAP5, Instruction.POP);
            Fuse<SwapStep<EvmInstructions.Op2>, BinaryStep<SubtractOperation>>(handlers, paired, Instruction.SWAP2, Instruction.SUB);
            Fuse<DupStep<EvmInstructions.Op7>, SwapStep<EvmInstructions.Op2>>(handlers, paired, Instruction.DUP7, Instruction.SWAP2);
            Fuse<DupStep<EvmInstructions.Op4>, BinaryStep<SubtractOperation>>(handlers, paired, Instruction.DUP4, Instruction.SUB);
            Fuse<DupStep<EvmInstructions.Op8>, SwapStep<EvmInstructions.Op2>>(handlers, paired, Instruction.DUP8, Instruction.SWAP2);
            Fuse<SwapStep<EvmInstructions.Op4>, PopStep>(handlers, paired, Instruction.SWAP4, Instruction.POP);
            Fuse<DupStep<EvmInstructions.Op5>, BinaryStep<SubtractOperation>>(handlers, paired, Instruction.DUP5, Instruction.SUB);
            Fuse<DupStep<EvmInstructions.Op2>, BinaryStep<SubtractOperation>>(handlers, paired, Instruction.DUP2, Instruction.SUB);
            Fuse<DupStep<EvmInstructions.Op6>, SwapStep<EvmInstructions.Op2>>(handlers, paired, Instruction.DUP6, Instruction.SWAP2);
            Fuse<BinaryStep<AddOperation>, DupStep<EvmInstructions.Op3>>(handlers, paired, Instruction.ADD, Instruction.DUP3);
            Fuse<SwapStep<EvmInstructions.Op3>, DupStep<EvmInstructions.Op4>>(handlers, paired, Instruction.SWAP3, Instruction.DUP4);
            Fuse<DupStep<EvmInstructions.Op3>, BinaryStep<SubtractOperation>>(handlers, paired, Instruction.DUP3, Instruction.SUB);
            Fuse<DupStep<EvmInstructions.Op4>, DupStep<EvmInstructions.Op4>>(handlers, paired, Instruction.DUP4, Instruction.DUP4);
            Fuse<PopStep, DupStep<EvmInstructions.Op2>>(handlers, paired, Instruction.POP, Instruction.DUP2);
            Fuse<SwapStep<EvmInstructions.Op3>, BinaryStep<AddOperation>>(handlers, paired, Instruction.SWAP3, Instruction.ADD);
            Fuse<DupStep<EvmInstructions.Op3>, DupStep<EvmInstructions.Op1>>(handlers, paired, Instruction.DUP3, Instruction.DUP1);
            Fuse<BinaryStep<AndOperation>, SwapStep<EvmInstructions.Op2>>(handlers, paired, Instruction.AND, Instruction.SWAP2);
            Fuse<DupStep<EvmInstructions.Op3>, SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.DUP3, Instruction.SWAP1);
            Fuse<SwapStep<EvmInstructions.Op1>, BinaryStep<SubtractOperation>>(handlers, paired, Instruction.SWAP1, Instruction.SUB);
            Fuse<DupStep<EvmInstructions.Op4>, BinaryStep<AndOperation>>(handlers, paired, Instruction.DUP4, Instruction.AND);
            Fuse<DupStep<EvmInstructions.Op2>, DupStep<EvmInstructions.Op1>>(handlers, paired, Instruction.DUP2, Instruction.DUP1);
            Fuse<PopStep, SwapStep<EvmInstructions.Op2>>(handlers, paired, Instruction.POP, Instruction.SWAP2);
            Fuse<BinaryStep<AddOperation>, DupStep<EvmInstructions.Op5>>(handlers, paired, Instruction.ADD, Instruction.DUP5);
            Fuse<SwapStep<EvmInstructions.Op2>, DupStep<EvmInstructions.Op2>>(handlers, paired, Instruction.SWAP2, Instruction.DUP2);
            Fuse<BinaryStep<AndOperation>, DupStep<EvmInstructions.Op1>>(handlers, paired, Instruction.AND, Instruction.DUP1);
            Fuse<DupStep<EvmInstructions.Op8>, BinaryStep<AddOperation>>(handlers, paired, Instruction.DUP8, Instruction.ADD);
            Fuse<SwapStep<EvmInstructions.Op2>, BinaryStep<AndOperation>>(handlers, paired, Instruction.SWAP2, Instruction.AND);
            Fuse<DupStep<EvmInstructions.Op2>, DupStep<EvmInstructions.Op6>>(handlers, paired, Instruction.DUP2, Instruction.DUP6);
            Fuse<SwapStep<EvmInstructions.Op5>, SwapStep<EvmInstructions.Op4>>(handlers, paired, Instruction.SWAP5, Instruction.SWAP4);
            Fuse<DupStep<EvmInstructions.Op2>, DupStep<EvmInstructions.Op7>>(handlers, paired, Instruction.DUP2, Instruction.DUP7);
            Fuse<BinaryStep<AddOperation>, SwapStep<EvmInstructions.Op4>>(handlers, paired, Instruction.ADD, Instruction.SWAP4);
            Fuse<SwapStep<EvmInstructions.Op4>, DupStep<EvmInstructions.Op5>>(handlers, paired, Instruction.SWAP4, Instruction.DUP5);
            Fuse<BinaryStep<SubtractOperation>, BinaryStep<AddOperation>>(handlers, paired, Instruction.SUB, Instruction.ADD);
            Fuse<DupStep<EvmInstructions.Op7>, DupStep<EvmInstructions.Op3>>(handlers, paired, Instruction.DUP7, Instruction.DUP3);
            Fuse<DupStep<EvmInstructions.Op7>, DupStep<EvmInstructions.Op7>>(handlers, paired, Instruction.DUP7, Instruction.DUP7);
            Fuse<DupStep<EvmInstructions.Op2>, DupStep<EvmInstructions.Op5>>(handlers, paired, Instruction.DUP2, Instruction.DUP5);
            Fuse<DupStep<EvmInstructions.Op4>, DupStep<EvmInstructions.Op6>>(handlers, paired, Instruction.DUP4, Instruction.DUP6);
            Fuse<DupStep<EvmInstructions.Op9>, BinaryStep<AddOperation>>(handlers, paired, Instruction.DUP9, Instruction.ADD);
            Fuse<DupStep<EvmInstructions.Op5>, DupStep<EvmInstructions.Op2>>(handlers, paired, Instruction.DUP5, Instruction.DUP2);
            Fuse<BinaryStep<SubtractOperation>, DupStep<EvmInstructions.Op4>>(handlers, paired, Instruction.SUB, Instruction.DUP4);
            Fuse<DupStep<EvmInstructions.Op4>, SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.DUP4, Instruction.SWAP1);
            Fuse<BinaryStep<AddOperation>, DupStep<EvmInstructions.Op4>>(handlers, paired, Instruction.ADD, Instruction.DUP4);
            Fuse<DupStep<EvmInstructions.Op5>, DupStep<EvmInstructions.Op5>>(handlers, paired, Instruction.DUP5, Instruction.DUP5);
            Fuse<BinaryStep<AndOperation>, SwapStep<EvmInstructions.Op3>>(handlers, paired, Instruction.AND, Instruction.SWAP3);
            Fuse<SwapStep<EvmInstructions.Op2>, SwapStep<EvmInstructions.Op3>>(handlers, paired, Instruction.SWAP2, Instruction.SWAP3);
            Fuse<DupStep<EvmInstructions.Op5>, SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.DUP5, Instruction.SWAP1);
            Fuse<SwapStep<EvmInstructions.Op6>, PopStep>(handlers, paired, Instruction.SWAP6, Instruction.POP);
        }

        /// <summary>Installs <see cref="ExecutePair{TFirst, TSecond}"/> for <paramref name="first"/> followed by <paramref name="second"/>.</summary>
        /// <remarks>Only where both opcodes run the handlers the steps stand in for, which a fork or a test can replace.</remarks>
        private static void Fuse<TFirst, TSecond>(ReadOnlySpan<nint> handlers, nint[] paired, Instruction first, Instruction second)
            where TFirst : struct, IStackStep
            where TSecond : struct, IStackStep
        {
            if (handlers[(int)first] == TFirst.Handler && handlers[(int)second] == TSecond.Handler)
                paired[(int)first | (int)second << 8] =
                    (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                    &ExecutePair<TFirst, TSecond>;
        }

        /// <summary>Two opcodes of fixed cost that only rearrange or combine the words on top of the stack, run as one step.</summary>
        /// <remarks>
        /// Saves the second opcode its dispatch and its own stack and gas tests. With too little gas or stack for both,
        /// the first runs alone through its own handler, so each opcode faults where it would unfused.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePair<TFirst, TSecond>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
            where TFirst : struct, IStackStep
            where TSecond : struct, IStackStep
        {
            if (Fits<TFirst, TSecond>(head) && TryCharge(ref gas, TFirst.GasCost + TSecond.GasCost))
            {
                ref ulong end = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head));
                TFirst.Apply(ref end);
                TSecond.Apply(ref Unsafe.Add(ref end, TFirst.Growth * LimbsPerWord));
                head += TFirst.Growth + TSecond.Growth;
                ip = ref Unsafe.Add(ref ip, 2);
                nint next = handlers[PairAt(ref ip)];
                return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
            }

            nint alone = TFirst.Handler;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, alone);
        }

        /// <summary>Whether a stack of <paramref name="head"/> words holds what both steps read, and has room for what they add.</summary>
        /// <remarks>Both bounds are constants, so a bounded range takes one unsigned test.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Fits<TFirst, TSecond>(nint head)
            where TFirst : struct, IStackStep
            where TSecond : struct, IStackStep
        {
            int lowest = Math.Max(TFirst.Inputs, TSecond.Inputs - TFirst.Growth);
            if (TFirst.Growth <= 0 && TSecond.Growth <= 0)
                return head >= lowest;

            // A step that grows the stack needs a free slot above the words it starts from.
            int limit = Math.Min(
                TFirst.Growth > 0 ? EvmStack.MaxStackSize - 1 : int.MaxValue,
                TSecond.Growth > 0 ? EvmStack.MaxStackSize - 1 - TFirst.Growth : int.MaxValue);
            return (nuint)(head - lowest) < (nuint)(limit - lowest);
        }

        /// <summary>An opcode that <see cref="ExecutePair{TFirst, TSecond}"/> can run as either step.</summary>
        internal interface IStackStep
        {
            /// <summary>How many words the opcode needs on the stack.</summary>
            static abstract int Inputs { get; }

            /// <summary>How many words the opcode adds to the stack, or removes from it when negative.</summary>
            static abstract int Growth { get; }

            /// <summary>The opcode's fixed gas cost.</summary>
            static abstract ulong GasCost { get; }

            /// <summary>The opcode's guest handler, which runs it alone.</summary>
            static abstract nint Handler { get; }

            /// <summary>Runs the opcode on the words below <paramref name="end"/>, the limb above the top word.</summary>
            static abstract void Apply(ref ulong end);
        }

        /// <summary>DUPn as a step of a fused pair.</summary>
        internal readonly struct DupStep<TOpCount> : IStackStep
            where TOpCount : struct, EvmInstructions.IOpCount
        {
            public static int Inputs => TOpCount.Count;
            public static int Growth => 1;
            public static ulong GasCost => VeryLowGasCost.GasCost;

            public static nint Handler
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => typeof(TOpCount) == typeof(EvmInstructions.Op1)
                    ? (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecuteDup1
                    : (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecuteDup<TOpCount>;
            }

            /// <remarks>The source is addressed off the copy itself, so each access folds its constant into its own offset.</remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                nint source = -TOpCount.Count * LimbsPerWord;
                end = Unsafe.Add(ref end, source);
                Unsafe.Add(ref end, 1) = Unsafe.Add(ref end, source + 1);
                Unsafe.Add(ref end, 2) = Unsafe.Add(ref end, source + 2);
                Unsafe.Add(ref end, 3) = Unsafe.Add(ref end, source + 3);
            }
        }

        /// <summary>SWAPn as a step of a fused pair.</summary>
        internal readonly struct SwapStep<TOpCount> : IStackStep
            where TOpCount : struct, EvmInstructions.IOpCount
        {
            public static int Inputs => TOpCount.Count + 1;
            public static int Growth => 0;
            public static ulong GasCost => VeryLowGasCost.GasCost;

            public static nint Handler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecuteSwap<TOpCount>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end)
            {
                SwapLimbs(ref end, -LimbsPerWord, -(TOpCount.Count + 1) * LimbsPerWord);
                SwapLimbs(ref end, 1 - LimbsPerWord, 1 - (TOpCount.Count + 1) * LimbsPerWord);
                SwapLimbs(ref end, 2 - LimbsPerWord, 2 - (TOpCount.Count + 1) * LimbsPerWord);
                SwapLimbs(ref end, 3 - LimbsPerWord, 3 - (TOpCount.Count + 1) * LimbsPerWord);
            }
        }

        /// <summary>POP as a step of a fused pair.</summary>
        internal readonly struct PopStep : IStackStep
        {
            public static int Inputs => 1;
            public static int Growth => -1;
            public static ulong GasCost => BaseGasCost.GasCost;

            public static nint Handler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecutePop;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end) { }
        }

        /// <summary>An operation <see cref="ExecuteBinary{TOperation}"/> runs, as a step of a fused pair.</summary>
        internal readonly struct BinaryStep<TOperation> : IStackStep
            where TOperation : struct, IStackBinaryOperation
        {
            public static int Inputs => 2;
            public static int Growth => -1;
            public static ulong GasCost => TOperation.GasCost;

            public static nint Handler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecuteBinary<TOperation>;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end) => TOperation.Apply(ref end);
        }
    }
}
