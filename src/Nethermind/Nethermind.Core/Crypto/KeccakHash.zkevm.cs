// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial bool TryComputeHash256Into(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(output), ComputeHash256(input));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void KeccakF(Span<ulong> st)
    {
        Debug.Assert(st.Length == STATE_LANES);
        Accelerators.KeccakF(ref MemoryMarshal.GetReference(st));
    }

    /// <inheritdoc cref="KeccakHash.InitializeState" />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void InitializeState(out KeccakState state, int inputLength, int roundSize)
    {
        Unsafe.SkipInit(out state);
        ref ulong lane = ref Unsafe.As<KeccakState, ulong>(ref state);

        // The rate block can be left alone only where ComputeHash reaches AbsorbFirstBlock, which writes
        // all seventeen of its lanes outright; every other rate width, and every input short enough to be
        // copied or XORed in, needs those lanes zero first.
        if (roundSize != HASH_DATA_AREA || inputLength < HASH_DATA_AREA)
        {
            Unsafe.Add(ref lane, 0) = 0;
            Unsafe.Add(ref lane, 1) = 0;
            Unsafe.Add(ref lane, 2) = 0;
            Unsafe.Add(ref lane, 3) = 0;
            Unsafe.Add(ref lane, 4) = 0;
            Unsafe.Add(ref lane, 5) = 0;
            Unsafe.Add(ref lane, 6) = 0;
            Unsafe.Add(ref lane, 7) = 0;
            Unsafe.Add(ref lane, 8) = 0;
            Unsafe.Add(ref lane, 9) = 0;
            Unsafe.Add(ref lane, 10) = 0;
            Unsafe.Add(ref lane, 11) = 0;
            Unsafe.Add(ref lane, 12) = 0;
            Unsafe.Add(ref lane, 13) = 0;
            Unsafe.Add(ref lane, 14) = 0;
            Unsafe.Add(ref lane, 15) = 0;
            Unsafe.Add(ref lane, 16) = 0;
        }

        // The capacity lanes are never absorbed into, so nothing else will write them.
        Unsafe.Add(ref lane, 17) = 0;
        Unsafe.Add(ref lane, 18) = 0;
        Unsafe.Add(ref lane, 19) = 0;
        Unsafe.Add(ref lane, 20) = 0;
        Unsafe.Add(ref lane, 21) = 0;
        Unsafe.Add(ref lane, 22) = 0;
        Unsafe.Add(ref lane, 23) = 0;
        Unsafe.Add(ref lane, 24) = 0;
    }

    /// <inheritdoc cref="KeccakHash.AbsorbMessageIntoZeroState" />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial ReadOnlySpan<byte> AbsorbMessageIntoZeroState(scoped Span<ulong> state, scoped Span<byte> stateBytes, ReadOnlySpan<byte> input, int roundSize)
    {
        AbsorbFirstBlock(stateBytes, input[..roundSize]);
        KeccakF(state);
        input = input[roundSize..];

        if (input.Length >= roundSize)
        {
            return AbsorbFullBlocks(state, stateBytes, input, roundSize);
        }

        AbsorbTail(stateBytes, input);
        return input;
    }

    /// <inheritdoc cref="KeccakHash.ComputeHash256" />
    /// <remarks>Every lane goes straight from the input into the precompile's state: the first block is
    /// written rather than XORed, a sub-rate tail is dispatched once on its lane count so each lane is a
    /// fixed-offset access rather than a loop iteration, and the partial last word, the 0x01 pad byte and
    /// the final 0x80 bit become whole-lane XORs rather than byte-wide read-modify-writes. The input is
    /// pinned so that the cursor is a plain pointer: the JIT spills every live GC ref around an unmanaged
    /// call, a transition-free one included, which would cost a store and a load per block.</remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static partial ValueHash256 ComputeHash256(ReadOnlySpan<byte> input)
    {
        unsafe
        {
            fixed (byte* data = input)
            {
                return ComputeHash256(data, (nuint)(uint)input.Length);
            }
        }
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ValueHash256 ComputeHash256(byte* data, nuint length)
    {
        if (length == Hash532InputLength && Retained.Slots is not null)
        {
            return ComputeHash532FromRetained(data);
        }

        Unsafe.SkipInit(out KeccakState stateBuffer);
        Span<ulong> state = stateBuffer;
        ref ulong lane = ref MemoryMarshal.GetReference(state);

        Unsafe.Add(ref lane, 17) = 0;
        Unsafe.Add(ref lane, 18) = 0;
        Unsafe.Add(ref lane, 19) = 0;
        Unsafe.Add(ref lane, 20) = 0;
        Unsafe.Add(ref lane, 21) = 0;
        Unsafe.Add(ref lane, 22) = 0;
        Unsafe.Add(ref lane, 23) = 0;
        Unsafe.Add(ref lane, 24) = 0;

        if (length == Hash532InputLength)
        {
            // A full branch node, the most common input of both the witness load and the trie commit:
            // spelled out, it skips the block loop, and its constant tail folds the lane dispatch away.
            AbsorbBlock(ref lane, data, intoZeroState: true);
            KeccakF(state);
            AbsorbBlock(ref lane, data + HASH_DATA_AREA, intoZeroState: false);
            KeccakF(state);
            AbsorbBlock(ref lane, data + 2 * HASH_DATA_AREA, intoZeroState: false);
            KeccakF(state);
            AbsorbPaddedTail(ref lane, data + 3 * HASH_DATA_AREA, Hash532InputLength - 3 * HASH_DATA_AREA);
        }
        else if (length >= HASH_DATA_AREA)
        {
            AbsorbBlock(ref lane, data, intoZeroState: true);
            KeccakF(state);
            data += HASH_DATA_AREA;
            length -= HASH_DATA_AREA;

            while (length >= HASH_DATA_AREA)
            {
                AbsorbBlock(ref lane, data, intoZeroState: false);
                KeccakF(state);
                data += HASH_DATA_AREA;
                length -= HASH_DATA_AREA;
            }

            AbsorbPaddedTail(ref lane, data, length);
        }
        else if (length == 32)
        {
            AbsorbShortFixed(ref lane, data, 32);
        }
        else if (length == 64)
        {
            AbsorbShortFixed(ref lane, data, 64);
        }
        else if (length == 20)
        {
            AbsorbShortFixed(ref lane, data, 20);
        }
        else
        {
            Unsafe.Add(ref lane, 0) = 0;
            Unsafe.Add(ref lane, 1) = 0;
            Unsafe.Add(ref lane, 2) = 0;
            Unsafe.Add(ref lane, 3) = 0;
            Unsafe.Add(ref lane, 4) = 0;
            Unsafe.Add(ref lane, 5) = 0;
            Unsafe.Add(ref lane, 6) = 0;
            Unsafe.Add(ref lane, 7) = 0;
            Unsafe.Add(ref lane, 8) = 0;
            Unsafe.Add(ref lane, 9) = 0;
            Unsafe.Add(ref lane, 10) = 0;
            Unsafe.Add(ref lane, 11) = 0;
            Unsafe.Add(ref lane, 12) = 0;
            Unsafe.Add(ref lane, 13) = 0;
            Unsafe.Add(ref lane, 14) = 0;
            Unsafe.Add(ref lane, 15) = 0;
            Unsafe.Add(ref lane, 16) = 0;

            AbsorbLanes(ref lane, data, length >> 3, intoZeroState: true);
            Unsafe.Add(ref lane, length >> 3) = length >= sizeof(ulong)
                ? PaddedLastWord(data + length, length & 7)
                : PaddedShortInput(data, length);
        }

        Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) = Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) ^ (0x80UL << 56);
        KeccakF(state);
        return Unsafe.As<ulong, ValueHash256>(ref lane);
    }

    /// <summary>Whole rate blocks of a full branch, each of which leaves a retained sponge state.</summary>
    private const int RetainedBlocks = Hash532InputLength / HASH_DATA_AREA;
    private const int RetainedStateLanes = RetainedBlocks * STATE_LANES;

    /// <summary>The byte offset of the word that places a retained input in <see cref="Retained.Slots"/>.</summary>
    /// <remarks>Past a branch's list prefix and its first child's string prefix, inside that child's hash, so it is
    /// uniformly distributed for an honest witness. A crafted one can crowd a slot, which only bounds the probe.</remarks>
    private const int RetainedKeyOffset = 8;

    private const int MaxRetainedProbes = 8;

    /// <summary>The inputs <see cref="ComputeHash256OfRetained"/> retained.</summary>
    /// <remarks>A class of its own, with no initializer, so a probe of <see cref="Slots"/> is a plain load rather
    /// than a class-initialization check on every full-branch hash.</remarks>
    private static class Retained
    {
        /// <summary>Per retained input, the sponge states after each of its whole rate blocks.</summary>
        public static ulong[]? States;
        public static byte[][]? Inputs;
        /// <summary>Open addressing, two words per slot: the input's key word, then its index in <see cref="Inputs"/> plus one.</summary>
        public static ulong[]? Slots;
        public static nuint SlotMask;
    }

    /// <inheritdoc cref="KeccakHash.ComputeHash256OfRetained" />
    /// <remarks>Only full-branch-length inputs are retained: when the trie commit re-encodes a witness branch, the
    /// edited child leaves every rate block before its own unchanged, and on a mainnet block those leading blocks
    /// are most of the commit's repeated permutations. Retaining costs a copy of the capacity lanes per block, as
    /// each block is absorbed into a fresh state rather than in place.</remarks>
    internal static partial void ComputeHash256OfRetained(ReadOnlySpan<byte[]> inputs, Span<ValueHash256> hashes)
    {
        int count = 0;
        for (int i = 0; i < inputs.Length; i++)
        {
            if (inputs[i].Length == Hash532InputLength) count++;
        }

        ulong[] states = new ulong[count * RetainedStateLanes];
        byte[][] retainedInputs = new byte[count][];
        int entry = 0;
        for (int i = 0; i < inputs.Length; i++)
        {
            byte[] input = inputs[i];
            if (input.Length == Hash532InputLength)
            {
                hashes[i] = ComputeHash532Retaining(input, ref states[entry * RetainedStateLanes]);
                retainedInputs[entry++] = input;
            }
            else
            {
                hashes[i] = ComputeHash256(input);
            }
        }

        Retain(retainedInputs, states);
    }

    /// <summary>Publishes full branches and the sponge states their whole rate blocks leave, replacing any retained before.</summary>
    /// <param name="inputs">Full branches, each <see cref="Hash532InputLength"/> bytes.</param>
    /// <param name="states">Per input, <see cref="RetainedBlocks"/> states of <see cref="STATE_LANES"/> lanes.</param>
    internal static void Retain(byte[][] inputs, ulong[] states)
    {
        int slotCount = (int)BitOperations.RoundUpToPowerOf2((uint)inputs.Length | 1) * 2;
        ulong[] slots = new ulong[slotCount * 2];
        nuint slotMask = (nuint)slotCount - 1;
        for (int entry = 0; entry < inputs.Length; entry++)
            InsertRetained(slots, slotMask, inputs[entry], entry);

        Retained.States = states;
        Retained.Inputs = inputs;
        Retained.SlotMask = slotMask;
        Retained.Slots = slots;
    }

    /// <summary>The retained state <see cref="ComputeHash256"/> would resume a full branch from.</summary>
    /// <param name="input">A full branch, <see cref="Hash532InputLength"/> bytes.</param>
    /// <param name="state">Receives that state, or nothing when no leading block matches.</param>
    /// <returns>How many leading whole rate blocks match a retained input, 0 when none does.</returns>
    /// <remarks>A seam for tests, which cannot reach <see cref="ComputeHash256"/>: in a ZK_EVM build the permutation is a
    /// zkVM precompile no host process can call.</remarks>
    internal static unsafe int FindRetainedState(ReadOnlySpan<byte> input, Span<ulong> state)
    {
        if (Retained.Slots is null) return 0;

        fixed (byte* data = input)
        {
            ref ulong retained = ref FindRetainedPrefix(data, out nuint blocks);
            if (blocks != 0)
                MemoryMarshal.CreateReadOnlySpan(ref retained, STATE_LANES).CopyTo(state);
            return (int)blocks;
        }
    }

    /// <summary>Hashes a full branch, leaving the state after each of its whole rate blocks in <paramref name="retained"/>.</summary>
    /// <param name="retained">Lane 0 of <see cref="RetainedBlocks"/> consecutive states, all zero.</param>
    [SkipLocalsInit]
    private static unsafe ValueHash256 ComputeHash532Retaining(byte[] input, ref ulong retained)
    {
        Unsafe.SkipInit(out KeccakState stateBuffer);
        ref ulong lane = ref Unsafe.As<KeccakState, ulong>(ref stateBuffer);
        ref ulong second = ref Unsafe.Add(ref retained, STATE_LANES);
        ref ulong third = ref Unsafe.Add(ref retained, 2 * STATE_LANES);

        fixed (byte* data = input)
        {
            // The first state's capacity lanes are zero already.
            AbsorbBlock(ref retained, data, intoZeroState: true);
            Accelerators.KeccakF(ref retained);
            AbsorbFrom(ref second, ref retained, data + HASH_DATA_AREA, HASH_DATA_AREA);
            Accelerators.KeccakF(ref second);
            AbsorbFrom(ref third, ref second, data + 2 * HASH_DATA_AREA, HASH_DATA_AREA);
            Accelerators.KeccakF(ref third);
            AbsorbFrom(ref lane, ref third, data + 3 * HASH_DATA_AREA, Hash532InputLength - 3 * HASH_DATA_AREA);
        }

        Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) = Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) ^ (0x80UL << 56);
        Accelerators.KeccakF(ref lane);
        return Unsafe.As<ulong, ValueHash256>(ref lane);
    }

    /// <summary>Places a retained input in the first free slot of its probe sequence, if any.</summary>
    /// <remarks>An input whose sequence is full is left unplaced: its hash is still right, only never resumed from.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertRetained(ulong[] slots, nuint slotMask, byte[] input, int entry)
    {
        ref ulong slotsRef = ref MemoryMarshal.GetArrayDataReference(slots);
        ulong key = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(input), RetainedKeyOffset));
        nuint slot = (nuint)key & slotMask;
        for (int probe = 0; probe < MaxRetainedProbes; probe++)
        {
            ref ulong entryWord = ref Unsafe.Add(ref slotsRef, slot * 2 + 1);
            if (entryWord == 0)
            {
                Unsafe.Add(ref slotsRef, slot * 2) = key;
                entryWord = (ulong)entry + 1;
                return;
            }

            slot = (slot + 1) & slotMask;
        }
    }

    /// <summary>Hashes a full branch from the retained state its leading rate blocks match, if any.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe ValueHash256 ComputeHash532FromRetained(byte* data)
    {
        Unsafe.SkipInit(out KeccakState stateBuffer);
        Span<ulong> state = stateBuffer;
        ref ulong lane = ref MemoryMarshal.GetReference(state);

        ref ulong retained = ref FindRetainedPrefix(data, out nuint blocks);
        switch (blocks)
        {
            case 0:
                Unsafe.Add(ref lane, 17) = 0;
                Unsafe.Add(ref lane, 18) = 0;
                Unsafe.Add(ref lane, 19) = 0;
                Unsafe.Add(ref lane, 20) = 0;
                Unsafe.Add(ref lane, 21) = 0;
                Unsafe.Add(ref lane, 22) = 0;
                Unsafe.Add(ref lane, 23) = 0;
                Unsafe.Add(ref lane, 24) = 0;
                AbsorbBlock(ref lane, data, intoZeroState: true);
                KeccakF(state);
                AbsorbBlock(ref lane, data + HASH_DATA_AREA, intoZeroState: false);
                KeccakF(state);
                AbsorbBlock(ref lane, data + 2 * HASH_DATA_AREA, intoZeroState: false);
                KeccakF(state);
                AbsorbPaddedTail(ref lane, data + 3 * HASH_DATA_AREA, Hash532InputLength - 3 * HASH_DATA_AREA);
                break;
            case 1:
                AbsorbFrom(ref lane, ref retained, data + HASH_DATA_AREA, HASH_DATA_AREA);
                KeccakF(state);
                AbsorbBlock(ref lane, data + 2 * HASH_DATA_AREA, intoZeroState: false);
                KeccakF(state);
                AbsorbPaddedTail(ref lane, data + 3 * HASH_DATA_AREA, Hash532InputLength - 3 * HASH_DATA_AREA);
                break;
            case 2:
                AbsorbFrom(ref lane, ref retained, data + 2 * HASH_DATA_AREA, HASH_DATA_AREA);
                KeccakF(state);
                AbsorbPaddedTail(ref lane, data + 3 * HASH_DATA_AREA, Hash532InputLength - 3 * HASH_DATA_AREA);
                break;
            default:
                AbsorbFrom(ref lane, ref retained, data + 3 * HASH_DATA_AREA, Hash532InputLength - 3 * HASH_DATA_AREA);
                break;
        }

        Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) = Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) ^ (0x80UL << 56);
        KeccakF(state);
        return Unsafe.As<ulong, ValueHash256>(ref lane);
    }

    /// <summary>Finds the retained input sharing the most leading rate blocks with a full branch.</summary>
    /// <param name="data">A full branch's <see cref="Hash532InputLength"/> bytes.</param>
    /// <param name="blocks">How many leading whole rate blocks match, 0 when none does.</param>
    /// <returns>The sponge state after those blocks, or a null reference when <paramref name="blocks"/> is 0.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ref ulong FindRetainedPrefix(byte* data, out nuint blocks)
    {
        ref ulong slots = ref MemoryMarshal.GetArrayDataReference(Retained.Slots!);
        nuint slotMask = Retained.SlotMask;
        ulong key = Unsafe.ReadUnaligned<ulong>(data + RetainedKeyOffset);
        nuint slot = (nuint)key & slotMask;
        for (int probe = 0; probe < MaxRetainedProbes; probe++)
        {
            ulong entry = Unsafe.Add(ref slots, slot * 2 + 1);
            if (entry == 0) break;

            if (Unsafe.Add(ref slots, slot * 2) == key)
            {
                nuint index = (nuint)entry - 1;
                ref byte input = ref MemoryMarshal.GetArrayDataReference(Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Retained.Inputs!), index));
                if (BlockEquals(data, ref input))
                {
                    nuint matched = 1;
                    if (BlockEquals(data + HASH_DATA_AREA, ref Unsafe.Add(ref input, HASH_DATA_AREA)))
                    {
                        matched = BlockEquals(data + 2 * HASH_DATA_AREA, ref Unsafe.Add(ref input, 2 * HASH_DATA_AREA)) ? 3u : 2u;
                    }

                    blocks = matched;
                    return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Retained.States!), index * RetainedStateLanes + (matched - 1) * STATE_LANES);
                }
            }

            slot = (slot + 1) & slotMask;
        }

        blocks = 0;
        return ref Unsafe.NullRef<ulong>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool BlockEquals(byte* data, ref byte retained) =>
        WordEquals(data, ref retained, 0)
        && WordEquals(data, ref retained, 1)
        && WordEquals(data, ref retained, 2)
        && WordEquals(data, ref retained, 3)
        && WordEquals(data, ref retained, 4)
        && WordEquals(data, ref retained, 5)
        && WordEquals(data, ref retained, 6)
        && WordEquals(data, ref retained, 7)
        && WordEquals(data, ref retained, 8)
        && WordEquals(data, ref retained, 9)
        && WordEquals(data, ref retained, 10)
        && WordEquals(data, ref retained, 11)
        && WordEquals(data, ref retained, 12)
        && WordEquals(data, ref retained, 13)
        && WordEquals(data, ref retained, 14)
        && WordEquals(data, ref retained, 15)
        && WordEquals(data, ref retained, 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool WordEquals(byte* data, ref byte retained, nuint index) =>
        Unsafe.ReadUnaligned<ulong>(data + index * sizeof(ulong)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref retained, index * sizeof(ulong)));

    /// <summary>Writes into <paramref name="lane"/> the state <paramref name="from"/> with <paramref name="length"/>
    /// bytes of <paramref name="data"/> XORed into its rate, and their 0x01 pad byte when they are fewer than a block.</summary>
    /// <param name="length">A constant from 8 to <see cref="HASH_DATA_AREA"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbFrom(ref ulong lane, ref ulong from, byte* data, nuint length)
    {
        AbsorbLaneFrom(ref lane, ref from, data, length, 0);
        AbsorbLaneFrom(ref lane, ref from, data, length, 1);
        AbsorbLaneFrom(ref lane, ref from, data, length, 2);
        AbsorbLaneFrom(ref lane, ref from, data, length, 3);
        AbsorbLaneFrom(ref lane, ref from, data, length, 4);
        AbsorbLaneFrom(ref lane, ref from, data, length, 5);
        AbsorbLaneFrom(ref lane, ref from, data, length, 6);
        AbsorbLaneFrom(ref lane, ref from, data, length, 7);
        AbsorbLaneFrom(ref lane, ref from, data, length, 8);
        AbsorbLaneFrom(ref lane, ref from, data, length, 9);
        AbsorbLaneFrom(ref lane, ref from, data, length, 10);
        AbsorbLaneFrom(ref lane, ref from, data, length, 11);
        AbsorbLaneFrom(ref lane, ref from, data, length, 12);
        AbsorbLaneFrom(ref lane, ref from, data, length, 13);
        AbsorbLaneFrom(ref lane, ref from, data, length, 14);
        AbsorbLaneFrom(ref lane, ref from, data, length, 15);
        AbsorbLaneFrom(ref lane, ref from, data, length, 16);
        Unsafe.Add(ref lane, 17) = Unsafe.Add(ref from, 17);
        Unsafe.Add(ref lane, 18) = Unsafe.Add(ref from, 18);
        Unsafe.Add(ref lane, 19) = Unsafe.Add(ref from, 19);
        Unsafe.Add(ref lane, 20) = Unsafe.Add(ref from, 20);
        Unsafe.Add(ref lane, 21) = Unsafe.Add(ref from, 21);
        Unsafe.Add(ref lane, 22) = Unsafe.Add(ref from, 22);
        Unsafe.Add(ref lane, 23) = Unsafe.Add(ref from, 23);
        Unsafe.Add(ref lane, 24) = Unsafe.Add(ref from, 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbLaneFrom(ref ulong lane, ref ulong from, byte* data, nuint length, nuint index) =>
        Unsafe.Add(ref lane, index) = Unsafe.Add(ref from, index) ^
            (length == HASH_DATA_AREA ? Unsafe.ReadUnaligned<ulong>(data + index * sizeof(ulong)) : ShortMessageLane(data, length, index));

    /// <summary>Writes every rate lane of a state from a sub-rate message and its 0x01 pad byte.</summary>
    /// <param name="length">A constant from 8 to 135: each lane then folds to one store of an input word,
    /// the padded last word or zero, rather than a zeroing pass and a lane dispatch.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe void AbsorbShortFixed(ref ulong lane, byte* data, nuint length)
    {
        Unsafe.Add(ref lane, 0) = ShortMessageLane(data, length, 0);
        Unsafe.Add(ref lane, 1) = ShortMessageLane(data, length, 1);
        Unsafe.Add(ref lane, 2) = ShortMessageLane(data, length, 2);
        Unsafe.Add(ref lane, 3) = ShortMessageLane(data, length, 3);
        Unsafe.Add(ref lane, 4) = ShortMessageLane(data, length, 4);
        Unsafe.Add(ref lane, 5) = ShortMessageLane(data, length, 5);
        Unsafe.Add(ref lane, 6) = ShortMessageLane(data, length, 6);
        Unsafe.Add(ref lane, 7) = ShortMessageLane(data, length, 7);
        Unsafe.Add(ref lane, 8) = ShortMessageLane(data, length, 8);
        Unsafe.Add(ref lane, 9) = ShortMessageLane(data, length, 9);
        Unsafe.Add(ref lane, 10) = ShortMessageLane(data, length, 10);
        Unsafe.Add(ref lane, 11) = ShortMessageLane(data, length, 11);
        Unsafe.Add(ref lane, 12) = ShortMessageLane(data, length, 12);
        Unsafe.Add(ref lane, 13) = ShortMessageLane(data, length, 13);
        Unsafe.Add(ref lane, 14) = ShortMessageLane(data, length, 14);
        Unsafe.Add(ref lane, 15) = ShortMessageLane(data, length, 15);
        Unsafe.Add(ref lane, 16) = ShortMessageLane(data, length, 16);
    }

    /// <summary>Rate lane <paramref name="index"/> of a sub-rate message of at least eight bytes followed by the 0x01 pad byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong ShortMessageLane(byte* data, nuint length, nuint index) =>
        index < length >> 3 ? Unsafe.ReadUnaligned<ulong>(data + index * sizeof(ulong))
        : index == length >> 3 ? PaddedLastWord(data + length, length & 7)
        : 0;

    /// <summary>XORs a sub-rate tail of <paramref name="length"/> bytes and the 0x01 pad byte after it into the state.</summary>
    /// <remarks>A whole block must precede the tail, which keeps the word ending the message in bounds.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbPaddedTail(ref ulong lane, byte* tail, nuint length)
    {
        AbsorbLanes(ref lane, tail, length >> 3, intoZeroState: false);
        ref ulong last = ref Unsafe.Add(ref lane, length >> 3);
        last = last ^ PaddedLastWord(tail + length, length & 7);
    }

    /// <summary>Absorbs a whole rate block of <paramref name="data"/>.</summary>
    /// <param name="intoZeroState">Whether the state's rate lanes are still zero, so the block is written rather than XORed.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbBlock(ref ulong lane, byte* data, bool intoZeroState)
    {
        AbsorbLane(ref lane, data, 0, intoZeroState);
        AbsorbLane(ref lane, data, 1, intoZeroState);
        AbsorbLane(ref lane, data, 2, intoZeroState);
        AbsorbLane(ref lane, data, 3, intoZeroState);
        AbsorbLane(ref lane, data, 4, intoZeroState);
        AbsorbLane(ref lane, data, 5, intoZeroState);
        AbsorbLane(ref lane, data, 6, intoZeroState);
        AbsorbLane(ref lane, data, 7, intoZeroState);
        AbsorbLane(ref lane, data, 8, intoZeroState);
        AbsorbLane(ref lane, data, 9, intoZeroState);
        AbsorbLane(ref lane, data, 10, intoZeroState);
        AbsorbLane(ref lane, data, 11, intoZeroState);
        AbsorbLane(ref lane, data, 12, intoZeroState);
        AbsorbLane(ref lane, data, 13, intoZeroState);
        AbsorbLane(ref lane, data, 14, intoZeroState);
        AbsorbLane(ref lane, data, 15, intoZeroState);
        AbsorbLane(ref lane, data, 16, intoZeroState);
    }

    /// <summary>Absorbs the first <paramref name="count"/> whole lanes of <paramref name="data"/>, at most sixteen.</summary>
    /// <param name="intoZeroState">Whether those state lanes are still zero, so the lanes are written rather than XORed.</param>
    /// <remarks>Falls through from the highest lane down, so each lane is a fixed-offset access behind a
    /// single jump-table dispatch rather than a loop.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbLanes(ref ulong lane, byte* data, nuint count, bool intoZeroState)
    {
        switch ((uint)count)
        {
            case 16: AbsorbLane(ref lane, data, 15, intoZeroState); goto case 15;
            case 15: AbsorbLane(ref lane, data, 14, intoZeroState); goto case 14;
            case 14: AbsorbLane(ref lane, data, 13, intoZeroState); goto case 13;
            case 13: AbsorbLane(ref lane, data, 12, intoZeroState); goto case 12;
            case 12: AbsorbLane(ref lane, data, 11, intoZeroState); goto case 11;
            case 11: AbsorbLane(ref lane, data, 10, intoZeroState); goto case 10;
            case 10: AbsorbLane(ref lane, data, 9, intoZeroState); goto case 9;
            case 9: AbsorbLane(ref lane, data, 8, intoZeroState); goto case 8;
            case 8: AbsorbLane(ref lane, data, 7, intoZeroState); goto case 7;
            case 7: AbsorbLane(ref lane, data, 6, intoZeroState); goto case 6;
            case 6: AbsorbLane(ref lane, data, 5, intoZeroState); goto case 5;
            case 5: AbsorbLane(ref lane, data, 4, intoZeroState); goto case 4;
            case 4: AbsorbLane(ref lane, data, 3, intoZeroState); goto case 3;
            case 3: AbsorbLane(ref lane, data, 2, intoZeroState); goto case 2;
            case 2: AbsorbLane(ref lane, data, 1, intoZeroState); goto case 1;
            case 1: AbsorbLane(ref lane, data, 0, intoZeroState); break;
            case 0: break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbLane(ref ulong lane, byte* data, nuint index, bool intoZeroState)
    {
        ulong word = Unsafe.ReadUnaligned<ulong>(data + index * sizeof(ulong));
        Unsafe.Add(ref lane, index) = intoZeroState ? word : Unsafe.Add(ref lane, index) ^ word;
    }

    /// <summary>The message's last <paramref name="partial"/> bytes followed by the 0x01 pad byte, as a lane.</summary>
    /// <param name="end">The end of the message, which must be at least eight bytes long.</param>
    /// <param name="partial">The bytes past the message's last whole lane, 0 to 7.</param>
    /// <remarks>Reads the whole word ending the message rather than the partial bytes one width at a time:
    /// on little-endian riscv64 those bytes are the word's top ones, so dropping its low byte, setting the
    /// pad byte above the rest and shifting the lot down by <c>7 - partial</c> bytes leaves exactly the
    /// lane to absorb.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong PaddedLastWord(byte* end, nuint partial)
    {
        ulong word = Unsafe.ReadUnaligned<ulong>(end - sizeof(ulong));
        return ((word >> 8) | (0x01UL << 56)) >> (int)((partial ^ 7) << 3);
    }

    /// <summary>A message shorter than a lane followed by the 0x01 pad byte, as a lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong PaddedShortInput(byte* data, nuint length)
    {
        ulong word = 0x01UL << (int)(length << 3);
        for (nuint i = 0; i < length; i++)
        {
            word |= (ulong)data[i] << (int)(i << 3);
        }

        return word;
    }

    /// <summary>Writes a whole rate block into a state that is still all-zero.</summary>
    /// <remarks>The assignment twin of the guest's unrolled absorb in <see cref="XorVectors"/>: same lane
    /// spelling, and the same alignment reasoning — the state is a <c>MemoryMarshal.AsBytes</c> of a
    /// <c>Span&lt;ulong&gt;</c>, hence ulong-aligned and safe to reinterpret, while the block is a
    /// caller-supplied span with no such guarantee, hence <c>ReadUnaligned</c>. A rate of any other width
    /// falls back to <see cref="XorVectors"/> — into an all-zero state, an XOR is a write. The one thing
    /// not shared is that sibling's <c>!Vector128.IsHardwareAccelerated</c> gate, which is there because
    /// <see cref="XorVectors"/> is compiled for the host as well; this file is not, and riscv64 has no
    /// vector width, so on the only target that reaches here the gate would be a constant.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AbsorbFirstBlock(Span<byte> state, ReadOnlySpan<byte> block)
    {
        if (block.Length != HASH_DATA_AREA)
        {
            XorVectors(state, block);
            return;
        }

        ref ulong st = ref Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(state));
        ref byte inRef = ref MemoryMarshal.GetReference(block);
        st = Unsafe.ReadUnaligned<ulong>(ref inRef);
        Unsafe.Add(ref st, 1) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 1 * sizeof(ulong)));
        Unsafe.Add(ref st, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 2 * sizeof(ulong)));
        Unsafe.Add(ref st, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 3 * sizeof(ulong)));
        Unsafe.Add(ref st, 4) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 4 * sizeof(ulong)));
        Unsafe.Add(ref st, 5) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 5 * sizeof(ulong)));
        Unsafe.Add(ref st, 6) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 6 * sizeof(ulong)));
        Unsafe.Add(ref st, 7) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 7 * sizeof(ulong)));
        Unsafe.Add(ref st, 8) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 8 * sizeof(ulong)));
        Unsafe.Add(ref st, 9) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 9 * sizeof(ulong)));
        Unsafe.Add(ref st, 10) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 10 * sizeof(ulong)));
        Unsafe.Add(ref st, 11) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 11 * sizeof(ulong)));
        Unsafe.Add(ref st, 12) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 12 * sizeof(ulong)));
        Unsafe.Add(ref st, 13) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 13 * sizeof(ulong)));
        Unsafe.Add(ref st, 14) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 14 * sizeof(ulong)));
        Unsafe.Add(ref st, 15) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 15 * sizeof(ulong)));
        Unsafe.Add(ref st, 16) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, 16 * sizeof(ulong)));
    }
}
