// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using static Nethermind.Evm.GuestWord;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    /// <summary>The number of entries in a paired table that dispatch indexes, one per opcode and the byte after it.</summary>
    internal const int PairedHandlersLength = 1 << 16;

    /// <summary>
    /// The number of entries ahead of the ones dispatch indexes in a paired table, one for each opcode a PUSH1 can be
    /// followed by.
    /// </summary>
    /// <remarks>They lie within a load's reach of the table's start, so reaching them takes no extra instruction.</remarks>
    internal const int FollowerHandlersLength = byte.MaxValue + 1;

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
    /// Expands a 256-entry table into the one the guest dispatches through, which from
    /// <see cref="FollowerHandlersLength"/> on is indexed by an opcode and the byte after it, read together as one
    /// little-endian <see cref="ushort"/>.
    /// </summary>
    /// <remarks>
    /// Each entry holds the opcode's handler from <paramref name="handlers"/>, unless the two bytes are a pair of opcodes
    /// that a fused handler runs as one step and both opcodes have the guest handlers the fused one stands in for. The
    /// table picks the fused handler at no cost, where testing the next opcode in the first one's handler would cost
    /// every occurrence of it. A fused handler is entered only at the first opcode, so a jump onto the second still runs
    /// it alone. The byte after a PUSH1 is its immediate, so a PUSH1 looks the opcode after that up in the entries ahead
    /// of the pairs instead.
    /// </remarks>
    internal static nint[] PairHandlers(ReadOnlySpan<nint> handlers)
    {
        nint[] paired = GC.AllocateUninitializedArray<nint>(FollowerHandlersLength + PairedHandlersLength);
        paired.AsSpan(0, FollowerHandlersLength).Fill(handlers[(int)Instruction.PUSH1]);
        for (int block = FollowerHandlersLength; block < paired.Length; block += byte.MaxValue + 1)
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
        /// <param name="handlers">The 256-entry table the paired one expands.</param>
        /// <param name="paired">The paired table, holding the handlers of <paramref name="handlers"/> on entry.</param>
        internal static void ConfigurePairs(ReadOnlySpan<nint> handlers, nint[] paired)
        {
            if (handlers[(int)Instruction.PUSH1] == Push1Step.Handler)
            {
                nint byFollower = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                    &ExecutePush1ByFollower;
                for (int immediate = 0; immediate <= byte.MaxValue; immediate++)
                    paired[FollowerHandlersLength + ((int)Instruction.PUSH1 | immediate << 8)] = byFollower;

                FuseAfterPush1<Push1Step>(handlers, paired, Instruction.PUSH1);
                FuseAfterPush1<BinaryStep<AddOperation>>(handlers, paired, Instruction.ADD);
                FuseAfterPush1<DupStep<EvmInstructions.Op2>>(handlers, paired, Instruction.DUP2);
                FuseAfterPush1<ShiftLeftStep>(handlers, paired, Instruction.SHL);
                FuseAfterPush1<DupStep<EvmInstructions.Op3>>(handlers, paired, Instruction.DUP3);
                FuseAfterPush1<SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.SWAP1);
                FuseAfterPush1<DupStep<EvmInstructions.Op1>>(handlers, paired, Instruction.DUP1);
                FuseAfterPush1<BinaryStep<ArithmeticShiftRightOperation>>(handlers, paired, Instruction.SAR);
                FuseAfterPush1<DupStep<EvmInstructions.Op4>>(handlers, paired, Instruction.DUP4);
                FuseAfterPush1<ShiftRightStep>(handlers, paired, Instruction.SHR);
                FuseAfterPush1<BinaryStep<SubtractOperation>>(handlers, paired, Instruction.SUB);
                FuseAfterPush1<BinaryStep<AndOperation>>(handlers, paired, Instruction.AND);
                FuseAfterPush1<DupStep<EvmInstructions.Op5>>(handlers, paired, Instruction.DUP5);
                FuseAfterPush1<SwapStep<EvmInstructions.Op2>>(handlers, paired, Instruction.SWAP2);
                FuseAfterPush1<BinaryStep<OrOperation>>(handlers, paired, Instruction.OR);
                FuseAfterPush1<BinaryStep<XorOperation>>(handlers, paired, Instruction.XOR);
                if (handlers[(int)Instruction.MLOAD] == (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecuteMLoadFromActiveMemory)
                    paired[(int)Instruction.MLOAD] = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecutePush1MLoad;
                if (handlers[(int)Instruction.MSTORE] == (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecuteMStoreInsideBacking)
                    paired[(int)Instruction.MSTORE] = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecutePush1MStore;
            }

            if (handlers[(int)Instruction.JUMP] == (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecuteJumpToAnalyzedDestination)
            {
                FuseBeforeJump<SwapStep<EvmInstructions.Op1>>(handlers, paired, Instruction.SWAP1);
                FuseBeforeJump<PopStep>(handlers, paired, Instruction.POP);
            }

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
                paired[FollowerHandlersLength + ((int)first | (int)second << 8)] =
                    (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                    &ExecutePair<TFirst, TSecond>;
        }

        /// <summary>Installs <see cref="ExecutePair{TFirst, TSecond}"/> for a PUSH1 followed by <paramref name="second"/>.</summary>
        private static void FuseAfterPush1<TSecond>(ReadOnlySpan<nint> handlers, nint[] paired, Instruction second)
            where TSecond : struct, IStackStep
        {
            if (handlers[(int)second] == TSecond.Handler)
                paired[(int)second] =
                    (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                    &ExecutePair<Push1Step, TSecond>;
        }

        /// <summary>Installs <see cref="ExecuteJumpAfter{TFirst}"/> for <paramref name="first"/> followed by JUMP.</summary>
        private static void FuseBeforeJump<TFirst>(ReadOnlySpan<nint> handlers, nint[] paired, Instruction first)
            where TFirst : struct, IStackStep
        {
            if (handlers[(int)first] == TFirst.Handler)
                paired[FollowerHandlersLength + ((int)first | (int)Instruction.JUMP << 8)] = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecuteJumpAfter<TFirst>;
        }

        /// <summary>
        /// SWAP1 or POP, fused with a JUMP after it onto a destination the incremental bitmap already holds, with the
        /// JUMPDEST it lands on.
        /// </summary>
        /// <remarks>
        /// Compilers return from an internal function with either: both leave the word the call pushed below the top,
        /// its return address, on top for the JUMP. Every other case, a short stack or gas included, runs the first
        /// opcode alone through its own handler.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteJumpAfter<TFirst>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
            where TFirst : struct, IStackStep
        {
            Debug.Assert(typeof(TFirst) == typeof(SwapStep<EvmInstructions.Op1>) || typeof(TFirst) == typeof(PopStep));
            ulong fusedGas = TFirst.GasCost + JumpGasCost.GasCost + JumpDestGasCost.GasCost;
            if (head > 1 && TryCharge(ref gas, fusedGas))
            {
                // The second word becomes the top for the JUMP either way.
                ref ulong destination = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 2));
                nuint target = (nuint)destination;
                if ((Unsafe.Add(ref destination, 1) | Unsafe.Add(ref destination, 2) | Unsafe.Add(ref destination, 3)) == 0 &&
                    target < (nuint)stack.CodeLength && stack.IsAnalyzedJumpDestination(target))
                {
                    // SWAP1 leaves the old top where the JUMP pops the destination from under it.
                    if (TFirst.Growth == 0)
                        CopyWord(ref Unsafe.Add(ref destination, LimbsPerWord), ref destination);

                    head += TFirst.Growth - 1;
                    ip = ref Unsafe.Add(ref code, (nint)target + 1);
                    nint next = handlers[PairAt(ref ip)];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
                }

                gas += fusedGas;
            }

            nint alone = TFirst.Handler;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, alone);
        }

        /// <summary>PUSH1 fused with an MLOAD after it of a word inside the active, initialized memory.</summary>
        /// <remarks>Every other case runs the PUSH1 alone, and the MLOAD handler after it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePush1MLoad(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            if (head < EvmStack.MaxStackSize - 1 && TryCharge(ref gas, 2 * VeryLowGasCost.GasCost))
            {
                ref byte source = ref state.Memory.GetActiveInitializedWord(Unsafe.Add(ref ip, 1));
                if (!Unsafe.IsNullRef(ref source))
                {
                    LoadBigEndian(ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head)), ref source);
                    head++;
                    ip = ref Unsafe.Add(ref ip, 3);
                    nint next = handlers[PairAt(ref ip)];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
                }

                gas += 2 * VeryLowGasCost.GasCost;
            }

            nint alone = Push1Step.Handler;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, alone);
        }

        /// <summary>PUSH1 fused with an MSTORE after it of the top word, to a word that needs no new backing.</summary>
        /// <remarks>Every other case runs the PUSH1 alone, and the MSTORE handler after it.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePush1MStore(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            // One unsigned test bounds the depth on both sides: the MSTORE needs a word below the pushed one.
            if ((nuint)(head - 1) < (nuint)(EvmStack.MaxStackSize - 2) && TryCharge(ref gas, 2 * VeryLowGasCost.GasCost))
            {
                // Charged in the carried gas: a separate copy would take a callee-saved register.
                ref byte destination = ref state.Memory.TryPrepareWordOverwrite(Unsafe.Add(ref ip, 1), ref gas);
                if (!Unsafe.IsNullRef(ref destination))
                {
                    // Memory holds the word big-endian.
                    ref ulong value = ref Unsafe.As<byte, ulong>(ref SlotAt(ref bottom, head - 1));
                    Unsafe.WriteUnaligned(ref destination, BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref value, 3)));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8), BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref value, 2)));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref value, 1)));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 24), BinaryPrimitives.ReverseEndianness(value));
                    head--;
                    ip = ref Unsafe.Add(ref ip, 3);
                    nint next = handlers[PairAt(ref ip)];
                    return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
                }

                gas += 2 * VeryLowGasCost.GasCost;
            }

            nint alone = Push1Step.Handler;
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, alone);
        }

        /// <summary>PUSH1, which goes on to the entry ahead of the pairs for the opcode after it: a fused pair, or <see cref="ExecutePush1"/>.</summary>
        /// <remarks>
        /// Most PUSH1s run in a fused pair with the opcode after them, so the extra dispatch the others take costs less
        /// than the dispatches the pairs save.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePush1ByFollower(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            ref byte ip,
            nint head,
            nint* handlers,
            ref byte code,
            ref byte bottom)
        {
            // Indexed first and offset after, so the offset folds into the load.
            nint next = Unsafe.Add(ref handlers[Unsafe.Add(ref ip, 2)], -FollowerHandlersLength);
            return TailDispatch(ref stack, gas, ref state, ref ip, head, handlers, ref code, ref bottom, next);
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
                if (typeof(TFirst) == typeof(Push1Step) && TSecond.TakesPushedByte)
                {
                    TSecond.ApplyToPushedByte(ref end, Unsafe.Add(ref ip, 1));
                }
                else
                {
                    TFirst.Apply(ref end, ref ip);
                    TSecond.Apply(ref Unsafe.Add(ref end, TFirst.Growth * LimbsPerWord), ref Unsafe.Add(ref ip, TFirst.Length));
                }
                head += TFirst.Growth + TSecond.Growth;
                ip = ref Unsafe.Add(ref ip, TFirst.Length + TSecond.Length);
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

            /// <summary>How many bytes the opcode takes in the code, its immediates included.</summary>
            static virtual int Length => 1;

            /// <summary>Runs the opcode on the words below <paramref name="end"/>, the limb above the top word.</summary>
            /// <param name="end">The limb above the top word.</param>
            /// <param name="opcode">The opcode in the code, which its immediates follow.</param>
            static abstract void Apply(ref ulong end, ref byte opcode);

            /// <summary>Whether <see cref="ApplyToPushedByte"/> runs the opcode after a PUSH1 without the pushed word.</summary>
            static virtual bool TakesPushedByte => false;

            /// <summary>Runs the opcode as if a PUSH1 of <paramref name="pushed"/> had just pushed it, without writing it.</summary>
            /// <param name="end">The limb above the top word before the push.</param>
            /// <param name="pushed">The PUSH1's immediate.</param>
            static virtual void ApplyToPushedByte(ref ulong end, ulong pushed) { }
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
            public static void Apply(ref ulong end, ref byte opcode)
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

            public static bool TakesPushedByte => true;

            /// <remarks>The word n deep comes up above the old top, and the pushed byte takes its place.</remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void ApplyToPushedByte(ref ulong end, ulong pushed)
            {
                nint source = -TOpCount.Count * LimbsPerWord;
                end = Unsafe.Add(ref end, source);
                Unsafe.Add(ref end, 1) = Unsafe.Add(ref end, source + 1);
                Unsafe.Add(ref end, 2) = Unsafe.Add(ref end, source + 2);
                Unsafe.Add(ref end, 3) = Unsafe.Add(ref end, source + 3);
                SetWord(ref Unsafe.Add(ref end, source), pushed);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end, ref byte opcode)
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
            public static void Apply(ref ulong end, ref byte opcode) { }
        }

        /// <summary>PUSH1 as a step of a fused pair.</summary>
        internal readonly struct Push1Step : IStackStep
        {
            public static int Inputs => 0;
            public static int Growth => 1;
            public static ulong GasCost => VeryLowGasCost.GasCost;
            public static int Length => 2;

            public static nint Handler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecutePush1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end, ref byte opcode) => SetWord(ref end, Unsafe.Add(ref opcode, 1));
        }

        /// <summary>SHL as a step of a fused pair; a shift of 256 or more clears the word.</summary>
        internal readonly struct ShiftLeftStep : IStackStep
        {
            public static int Inputs => 2;
            public static int Growth => -1;
            public static ulong GasCost => VeryLowGasCost.GasCost;
            public static bool TakesPushedByte => true;

            /// <remarks>A pushed byte is a shift below 256.</remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void ApplyToPushedByte(ref ulong end, ulong pushed) => ShiftLeft(ref Unsafe.Subtract(ref end, LimbsPerWord), (int)pushed);

            public static nint Handler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecuteShl;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end, ref byte opcode)
            {
                ref ulong shift = ref Unsafe.Subtract(ref end, LimbsPerWord);
                ref ulong value = ref Unsafe.Subtract(ref shift, LimbsPerWord);
                ulong bits = shift;
                if ((Unsafe.Add(ref shift, 1) | Unsafe.Add(ref shift, 2) | Unsafe.Add(ref shift, 3) | (bits >> 8)) == 0)
                    ShiftLeft(ref value, (int)bits);
                else
                    SetWord(ref value, 0);
            }
        }

        /// <summary>SHR as a step of a fused pair; a shift of 256 or more clears the word.</summary>
        internal readonly struct ShiftRightStep : IStackStep
        {
            public static int Inputs => 2;
            public static int Growth => -1;
            public static ulong GasCost => VeryLowGasCost.GasCost;
            public static bool TakesPushedByte => true;

            /// <remarks>A pushed byte is a shift below 256.</remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void ApplyToPushedByte(ref ulong end, ulong pushed) => ShiftRight(ref Unsafe.Subtract(ref end, LimbsPerWord), (int)pushed);

            public static nint Handler =>
                (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)&ExecuteShr;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Apply(ref ulong end, ref byte opcode)
            {
                ref ulong shift = ref Unsafe.Subtract(ref end, LimbsPerWord);
                ref ulong value = ref Unsafe.Subtract(ref shift, LimbsPerWord);
                ulong bits = shift;
                if ((Unsafe.Add(ref shift, 1) | Unsafe.Add(ref shift, 2) | Unsafe.Add(ref shift, 3) | (bits >> 8)) == 0)
                    ShiftRight(ref value, (int)bits);
                else
                    SetWord(ref value, 0);
            }
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
            public static void Apply(ref ulong end, ref byte opcode) => TOperation.Apply(ref end);

            public static bool TakesPushedByte =>
                typeof(TOperation) == typeof(AddOperation) || typeof(TOperation) == typeof(SubtractOperation) ||
                typeof(TOperation) == typeof(AndOperation) || typeof(TOperation) == typeof(OrOperation) ||
                typeof(TOperation) == typeof(XorOperation) || typeof(TOperation) == typeof(ArithmeticShiftRightOperation);

            /// <remarks>The pushed byte is the top operand and the old top word the second, which the result replaces.</remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void ApplyToPushedByte(ref ulong end, ulong pushed)
            {
                ref ulong word = ref Unsafe.Subtract(ref end, LimbsPerWord);
                if (typeof(TOperation) == typeof(AddOperation))
                {
                    ulong sum = word + pushed;
                    word = sum;
                    // A carry out of the low limb runs on until it meets a limb it does not wrap.
                    if (sum < pushed && ++Unsafe.Add(ref word, 1) == 0 && ++Unsafe.Add(ref word, 2) == 0)
                        Unsafe.Add(ref word, 3)++;
                }
                else if (typeof(TOperation) == typeof(SubtractOperation))
                {
                    // The pushed byte less the old top word, whose upper limbs only take from zero.
                    ulong low = word;
                    ulong borrow = pushed < low ? 1UL : 0UL;
                    word = pushed - low;
                    ulong limb = Unsafe.Add(ref word, 1);
                    Unsafe.Add(ref word, 1) = 0 - limb - borrow;
                    borrow = (limb | borrow) != 0 ? 1UL : 0UL;
                    limb = Unsafe.Add(ref word, 2);
                    Unsafe.Add(ref word, 2) = 0 - limb - borrow;
                    borrow = (limb | borrow) != 0 ? 1UL : 0UL;
                    Unsafe.Add(ref word, 3) = 0 - Unsafe.Add(ref word, 3) - borrow;
                }
                else if (typeof(TOperation) == typeof(AndOperation))
                {
                    SetWord(ref word, word & pushed);
                }
                else if (typeof(TOperation) == typeof(OrOperation))
                {
                    word |= pushed;
                }
                else if (typeof(TOperation) == typeof(XorOperation))
                {
                    word ^= pushed;
                }
                else if (typeof(TOperation) == typeof(ArithmeticShiftRightOperation))
                {
                    ShiftRight(ref word, (int)pushed, arithmetic: true);
                }
            }
        }
    }
}
