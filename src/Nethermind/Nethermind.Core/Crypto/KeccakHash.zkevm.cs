// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
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
                if (StrictAlignmentFlag.IsActive && ((nuint)data & 7) == 0)
                {
                    return ComputeHash256(AlignedDown(data), (nuint)(uint)input.Length);
                }

                return ComputeHash256(data, (nuint)(uint)input.Length);
            }
        }
    }

    /// <summary>Computes the Keccak-256 digest of the <paramref name="length"/> bytes at <paramref name="input"/> into <paramref name="hash"/>.</summary>
    /// <param name="length">A constant, so the sponge folds to that length's path.</param>
    /// <remarks>Written through a reference rather than returned: a returned digest goes through a stack temporary
    /// before it reaches memory elsewhere.</remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe void ComputeHash256Fixed(ref byte input, nuint length, out ValueHash256 hash)
    {
        Unsafe.SkipInit(out KeccakState stateBuffer);
        ref ulong lane = ref Unsafe.As<KeccakState, ulong>(ref stateBuffer);
        fixed (byte* data = &input)
        {
            Sponge(ref lane, StrictAlignmentFlag.IsActive && ((nuint)data & 7) == 0 ? AlignedDown(data) : data, length);
        }

        hash = Unsafe.As<ulong, ValueHash256>(ref lane);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ValueHash256 ComputeHash256(byte* data, nuint length)
    {
        Unsafe.SkipInit(out KeccakState stateBuffer);
        ref ulong lane = ref Unsafe.As<KeccakState, ulong>(ref stateBuffer);
        Sponge(ref lane, data, length);
        return Unsafe.As<ulong, ValueHash256>(ref lane);
    }

    /// <summary>Absorbs and pads the <paramref name="length"/> bytes at <paramref name="data"/> into a state of
    /// uninitialized lanes, leaving their digest in its first four lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Sponge(ref ulong lane, byte* data, nuint length)
    {
        if (length == Hash532InputLength)
        {
            // A full branch node, the most common input of both the witness load and the trie commit:
            // spelled out, it skips the block loop, and its constant tail folds the lane dispatch away.
            ZeroCapacity(ref lane);
            CopyFirstBlock(ref lane, data);
            Accelerators.KeccakF(ref lane);
            AbsorbBlock(ref lane, data + HASH_DATA_AREA, intoZeroState: false);
            Accelerators.KeccakF(ref lane);
            AbsorbBlock(ref lane, data + 2 * HASH_DATA_AREA, intoZeroState: false);
            Accelerators.KeccakF(ref lane);
            AbsorbPaddedTail(ref lane, data + 3 * HASH_DATA_AREA, Hash532InputLength - 3 * HASH_DATA_AREA);
        }
        else if (length >= HASH_DATA_AREA)
        {
            // Bounded by the last whole block's start rather than a remaining length, which the JIT spilled around every
            // permutation, or the end, which costs a subtraction and the rate's constant per block.
            byte* lastBlock = data + (length - HASH_DATA_AREA);
            ZeroCapacity(ref lane);
            CopyFirstBlock(ref lane, data);
            Accelerators.KeccakF(ref lane);
            data += HASH_DATA_AREA;

            // Two blocks per iteration halve the loop's compare and pointer bump on long inputs such as code.
            byte* lastPair = lastBlock - HASH_DATA_AREA;
            while (data <= lastPair)
            {
                AbsorbBlock(ref lane, data, intoZeroState: false);
                Accelerators.KeccakF(ref lane);
                AbsorbBlock(ref lane, data + HASH_DATA_AREA, intoZeroState: false);
                Accelerators.KeccakF(ref lane);
                data += 2 * HASH_DATA_AREA;
            }

            if (data <= lastBlock)
            {
                AbsorbBlock(ref lane, data, intoZeroState: false);
                Accelerators.KeccakF(ref lane);
                data += HASH_DATA_AREA;
            }

            AbsorbPaddedTail(ref lane, data, (nuint)(lastBlock - data) + HASH_DATA_AREA);
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
            ZeroState(ref lane);

            AbsorbLanes(ref lane, data, length >> 3, intoZeroState: true);
            Unsafe.Add(ref lane, length >> 3) = length >= sizeof(ulong)
                ? PaddedLastWord(data + length, length & 7)
                : PaddedShortInput(data, length);
        }

        Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) = Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) ^ (0x80UL << 56);
        Accelerators.KeccakF(ref lane);
    }

    /// <summary>The most leading rate blocks of a witness node whose sponge states <see cref="ComputeHash256OfWitnessNodes"/>
    /// keeps: all those of a full branch.</summary>
    private const int RetainedBlocks = Hash532InputLength / HASH_DATA_AREA;
    private const int RetainedLanes = RetainedBlocks * STATE_LANES;
    private const int FullBranchTailLength = Hash532InputLength - RetainedBlocks * HASH_DATA_AREA;
    private const int RateLanes = HASH_DATA_AREA / sizeof(ulong);
    private const int CapacityBytes = (STATE_LANES - RateLanes) * sizeof(ulong);

    /// <summary>Whether <see cref="ComputeHash256OfWitnessNodes"/> keeps the states of a node of <paramref name="length"/>
    /// bytes, so that <see cref="ComputeHash256OfEdited"/> can resume it.</summary>
    /// <remarks>A full branch always. A shorter one of at least two whole rate blocks only on ZisK, where the state copies
    /// that keeping them takes are DMA moves; on SP1 they go lane by lane and cost more than the permutations they save. A
    /// branch of a single whole block is edited too rarely to repay them anywhere. <see cref="TryAbsorbEdited"/> relies on
    /// this two-block floor: it compares the second block without a length check.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsResumable(int length) =>
        length == Hash532InputLength || ZiskFlag.IsActive && length >= 2 * HASH_DATA_AREA;

    /// <summary>The witness nodes <see cref="ComputeHash256OfWitnessNodes"/> keyed last, and what it kept of them.</summary>
    /// <remarks>A class of its own, with no initializer, so each access is a plain load or store rather than a
    /// class-initialization check.</remarks>
    private static class WitnessNodes
    {
        public static byte[][]? Nodes;
        /// <summary>How many of <see cref="Nodes"/> were keyed, those with an entry in <see cref="States"/>.</summary>
        public static nint Count;
        /// <summary>Per node, <see cref="RetainedLanes"/> lanes: the states its leading whole rate blocks leave, up to <see cref="RetainedBlocks"/>.</summary>
        /// <remarks>Packed where the guest reuses its input: only nodes that keep states have them, at the index
        /// <see cref="Slots"/> holds.</remarks>
        public static unsafe ulong* States;
        /// <summary>Where the guest reuses its input, per node that keeps states, the index of its states in <see cref="States"/>.</summary>
        public static unsafe uint* Slots;
        /// <summary>The tag <see cref="NoteWitnessNodeLoaded"/> was given last.</summary>
        public static nint Loaded;
    }

    /// <summary>The part of the input <see cref="DonateConsumedInput"/> was given that no states occupy yet.</summary>
    private static class FreeInput
    {
        public static unsafe byte* Start;
        public static nuint Length;
    }

    /// <inheritdoc cref="KeccakHash.DonateConsumedInput" />
    /// <remarks>Only where <see cref="FirstTouchMemoryFlag"/> is on, which assumes the input lies in writable memory, as
    /// on SP1. SP1 charges for every word of memory a run touches, far more than for the instructions writing it, and
    /// the input is touched already, so states kept in it are nearly free while fresh memory for them is not.</remarks>
    internal static partial void DonateConsumedInput(ReadOnlySpan<byte> consumed)
    {
        if (!FirstTouchMemoryFlag.IsActive) return;

        unsafe
        {
            nuint start = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(consumed));
            nuint aligned = (start + 7) & ~(nuint)7;
            nuint end = start + (nuint)consumed.Length;
            FreeInput.Start = (byte*)aligned;
            FreeInput.Length = end > aligned ? end - aligned : 0;
        }
    }

    /// <inheritdoc cref="KeccakHash.ComputeHash256OfWitnessNodes" />
    /// <remarks>When the trie commit re-encodes a witness branch, the edited children leave every rate block before
    /// the first of them unchanged; on a mainnet block the blocks so repeated are most of the commit's repeated
    /// permutations. Keeping them costs a copy of the capacity lanes per block, each block being absorbed into a
    /// state of its own.</remarks>
    internal static partial void ComputeHash256OfWitnessNodes(byte[][] nodes, int count, Span<ValueHash256> hashes)
    {
        if (FirstTouchMemoryFlag.IsActive)
        {
            ComputeHash256OfWitnessNodesPacked(nodes, count, hashes);
            return;
        }

        unsafe
        {
            // Unchecked, the counts checked once: a bounds check per node loads a 32-bit length, a narrow access.
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)count, (uint)Math.Min(nodes.Length, hashes.Length));
            // Native rather than a managed array, which the guest's allocator zeroes word by word. Left uninitialized: only
            // the states of a node's whole leading blocks are written, and ComputeHash256OfEdited reads no other.
            ulong* states = (ulong*)NativeMemory.Alloc((nuint)count, RetainedLanes * sizeof(ulong));
            Debug.Assert(((nuint)states & 7) == 0);
            ref byte[] firstNode = ref MemoryMarshal.GetArrayDataReference(nodes);
            ref ValueHash256 firstHash = ref MemoryMarshal.GetReference(hashes);
            Unsafe.SkipInit(out KeccakState stateBuffer);
            ref ulong lane = ref Unsafe.As<KeccakState, ulong>(ref stateBuffer);
            for (nint i = 0; i < count; i++)
            {
                byte[] node = Unsafe.Add(ref firstNode, i);
                ref ValueHash256 hash = ref Unsafe.Add(ref firstHash, i);
                // Each digest goes straight into its slot: a returned one was copied there through two temporaries.
                if (node.Length == Hash532InputLength)
                {
                    ComputeHash532Retaining(node, ref states[i * RetainedLanes], ref hash);
                    continue;
                }

                if (IsResumable(node.Length))
                {
                    ComputeHashRetaining(node, ref states[i * RetainedLanes], ref hash);
                    continue;
                }

                fixed (byte* data = node)
                {
                    Debug.Assert(((nuint)data & 7) == 0);
                    // An array's data is 8-byte aligned; the rounding lets the JIT prove it.
                    Sponge(ref lane, StrictAlignmentFlag.IsActive ? AlignedDown(data) : data, (nuint)node.Length);
                }

                hash = Unsafe.As<ulong, ValueHash256>(ref lane);
            }

            ulong* released = WitnessNodes.States;
            WitnessNodes.Nodes = nodes;
            WitnessNodes.Count = count;
            WitnessNodes.States = states;
            NativeMemory.Free(released);
        }
    }

    /// <summary><see cref="ComputeHash256OfWitnessNodes"/> where the guest reuses its input.</summary>
    /// <remarks>Keeps states only for the nodes <see cref="IsResumable"/> accepts, packed, and records where each one's
    /// are: about half the nodes keep none, and the room in the input left to them is short of a slot for every node.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void ComputeHash256OfWitnessNodesPacked(byte[][] nodes, int count, Span<ValueHash256> hashes)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)count, (uint)Math.Min(nodes.Length, hashes.Length));
        ref byte[] firstNode = ref MemoryMarshal.GetArrayDataReference(nodes);
        ref ValueHash256 firstHash = ref MemoryMarshal.GetReference(hashes);
        nuint resumable = 0;
        for (nint i = 0; i < count; i++)
        {
            if (IsResumable(Unsafe.Add(ref firstNode, i).Length)) resumable++;
        }

        ulong* states = (ulong*)AllocateInInput(resumable * (RetainedLanes * sizeof(ulong)) + (nuint)count * sizeof(uint));
        uint* slots = (uint*)(states + resumable * RetainedLanes);
        Unsafe.SkipInit(out KeccakState stateBuffer);
        ref ulong lane = ref Unsafe.As<KeccakState, ulong>(ref stateBuffer);
        uint slot = 0;
        for (nint i = 0; i < count; i++)
        {
            byte[] node = Unsafe.Add(ref firstNode, i);
            ref ValueHash256 hash = ref Unsafe.Add(ref firstHash, i);
            if (IsResumable(node.Length))
            {
                ref ulong retained = ref states[slot * (nuint)RetainedLanes];
                if (node.Length == Hash532InputLength) ComputeHash532Retaining(node, ref retained, ref hash);
                else ComputeHashRetaining(node, ref retained, ref hash);
                slots[i] = slot++;
                continue;
            }

            fixed (byte* data = node)
            {
                Sponge(ref lane, StrictAlignmentFlag.IsActive ? AlignedDown(data) : data, (nuint)node.Length);
            }

            hash = Unsafe.As<ulong, ValueHash256>(ref lane);
        }

        WitnessNodes.Nodes = nodes;
        WitnessNodes.Count = count;
        WitnessNodes.States = states;
        WitnessNodes.Slots = slots;
    }

    /// <summary>Allocates <paramref name="size"/> bytes, in the input <see cref="DonateConsumedInput"/> was given while it
    /// has room for them.</summary>
    private static unsafe void* AllocateInInput(nuint size)
    {
        size = (size + 7) & ~(nuint)7;
        if (size > FreeInput.Length)
            return NativeMemory.Alloc(size);

        byte* allocated = FreeInput.Start;
        FreeInput.Start += size;
        FreeInput.Length -= size;
        return allocated;
    }

    /// <inheritdoc cref="KeccakHash.NoteWitnessNodeLoaded" />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static partial void NoteWitnessNodeLoaded(nint tag) => WitnessNodes.Loaded = tag;

    /// <summary>The tag <see cref="NoteWitnessNodeLoaded"/> was given last.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static nint LoadedWitnessNode() => WitnessNodes.Loaded;

    /// <summary>Computes the Keccak-256 digest of a node re-encoded from <paramref name="previous"/>.</summary>
    /// <param name="input">The re-encoded node, of a length <see cref="IsResumable"/> accepts.</param>
    /// <param name="previous">The encoding <paramref name="input"/> was re-encoded from.</param>
    /// <param name="tag">What <see cref="LoadedWitnessNode"/> returned when <paramref name="previous"/> was loaded.</param>
    /// <remarks>Starts after the leading rate blocks <paramref name="input"/> shares with <paramref name="previous"/>,
    /// from the state <see cref="ComputeHash256OfWitnessNodes"/> kept, when <paramref name="previous"/> is the keyed node
    /// <paramref name="tag"/> names. Anything else, a stale tag included, hashes <paramref name="input"/> whole. An RLP
    /// list's first block holds its length, so an encoding of another length shares no block with it.</remarks>
    [SkipLocalsInit]
    internal static unsafe ValueHash256 ComputeHash256OfEdited(ReadOnlySpan<byte> input, byte[]? previous, nint tag)
    {
        byte[][]? nodes = WitnessNodes.Nodes;
        nuint index = (nuint)(tag - 1);
        // An equal resumable length guarantees the keyed node's states were written, not left uninitialized.
        if (nodes is null || index >= (nuint)WitnessNodes.Count || !ReferenceEquals(nodes[index], previous)
            || previous!.Length != input.Length || !IsResumable(input.Length))
        {
            return ValueKeccak.Compute(input);
        }

        nuint length = (nuint)input.Length;
        Unsafe.SkipInit(out KeccakState stateBuffer);
        ref ulong lane = ref Unsafe.As<KeccakState, ulong>(ref stateBuffer);
        fixed (byte* data = input, original = previous)
        {
            bool absorbed = StrictAlignmentFlag.IsActive && ((nuint)data & 7) == 0
                ? TryAbsorbEdited(ref lane, AlignedDown(data), AlignedDown(original), index, length)
                : TryAbsorbEdited(ref lane, data, original, index, length);
            if (!absorbed)
            {
                return ValueKeccak.Compute(input);
            }
        }

        Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) = Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) ^ (0x80UL << 56);
        Accelerators.KeccakF(ref lane);
        return Unsafe.As<ulong, ValueHash256>(ref lane);
    }

    /// <summary>Absorbs all of the <paramref name="length"/>-byte node <paramref name="data"/> but its pad byte into
    /// <paramref name="lane"/>, from the state <see cref="ComputeHash256OfWitnessNodes"/> kept for the leading rate blocks
    /// it shares with <paramref name="original"/>.</summary>
    /// <returns>Whether any leading block is shared; if none is, <paramref name="lane"/> is left as it was.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool TryAbsorbEdited(ref ulong lane, byte* data, byte* original, nuint index, nuint length)
    {
        // Spelled out over the at most three retained blocks rather than looped: a loop's compare call spilled and
        // reloaded every value it carries on each iteration.
        Debug.Assert(RetainedBlocks == 3 && length >= 2 * HASH_DATA_AREA, "A resumable node has two to three retained blocks");
        if (!BlockEquals(data, original))
        {
            return false;
        }

        nuint offset = HASH_DATA_AREA;
        nuint last = 0;
        if (BlockEquals(data + HASH_DATA_AREA, original + HASH_DATA_AREA))
        {
            offset = 2 * HASH_DATA_AREA;
            last = STATE_LANES;
            if (length >= RetainedBlocks * HASH_DATA_AREA && BlockEquals(data + 2 * HASH_DATA_AREA, original + 2 * HASH_DATA_AREA))
            {
                offset = RetainedBlocks * HASH_DATA_AREA;
                last = 2 * STATE_LANES;
            }
        }

        ulong* states = StrictAlignmentFlag.IsActive ? AlignedDown(WitnessNodes.States) : WitnessNodes.States;
        ref ulong retained = ref states[(FirstTouchMemoryFlag.IsActive ? WitnessNodes.Slots[index] : index) * RetainedLanes + last];
        if (length != Hash532InputLength)
        {
            AbsorbRestFrom(ref lane, ref retained, data + offset, length - offset);
        }
        else if (offset == RetainedBlocks * HASH_DATA_AREA)
        {
            AbsorbFullBranchTailFrom(ref lane, ref retained, data + offset);
        }
        else
        {
            AbsorbBlockFrom(ref lane, ref retained, data + offset);
            Accelerators.KeccakF(ref lane);
            for (offset += HASH_DATA_AREA; offset < RetainedBlocks * HASH_DATA_AREA; offset += HASH_DATA_AREA)
            {
                AbsorbBlock(ref lane, data + offset, intoZeroState: false);
                Accelerators.KeccakF(ref lane);
            }

            AbsorbPaddedTail(ref lane, data + offset, FullBranchTailLength);
        }

        return true;
    }

    /// <summary>Hashes a full branch into <paramref name="hash"/>, leaving the state after each of its leading rate blocks in <paramref name="retained"/>.</summary>
    /// <param name="retained">Lane 0 of <see cref="RetainedBlocks"/> consecutive states.</param>
    /// <remarks>Inlined: the keying loops run it for about half the nodes, and with two of them calling it, it is no
    /// longer inlined on its own.</remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ComputeHash532Retaining(byte[] node, ref ulong retained, ref ValueHash256 hash)
    {
        Unsafe.SkipInit(out KeccakState stateBuffer);
        ref ulong lane = ref Unsafe.As<KeccakState, ulong>(ref stateBuffer);
        ref ulong first = ref StrictAlignmentFlag.IsActive ? ref *AlignedDown((ulong*)Unsafe.AsPointer(ref retained)) : ref retained;

        fixed (byte* data = node)
        {
            // The later states are addressed off the first rather than held: the keying loop has no registers to keep them in.
            ZeroCapacity(ref first);
            AbsorbBlock(ref first, data, intoZeroState: true);
            Accelerators.KeccakF(ref first);
            AbsorbBlockFrom(ref Unsafe.Add(ref first, STATE_LANES), ref first, data + HASH_DATA_AREA);
            Accelerators.KeccakF(ref Unsafe.Add(ref first, STATE_LANES));
            AbsorbBlockFrom(ref Unsafe.Add(ref first, 2 * STATE_LANES), ref Unsafe.Add(ref first, STATE_LANES), data + 2 * HASH_DATA_AREA);
            Accelerators.KeccakF(ref Unsafe.Add(ref first, 2 * STATE_LANES));
            AbsorbFullBranchTailFrom(ref lane, ref Unsafe.Add(ref first, 2 * STATE_LANES), data + RetainedBlocks * HASH_DATA_AREA);
        }

        Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) = Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) ^ (0x80UL << 56);
        Accelerators.KeccakF(ref lane);
        hash = Unsafe.As<ulong, ValueHash256>(ref lane);
    }

    /// <summary>Hashes a node of another length than a full branch into <paramref name="hash"/>, leaving the state after
    /// each of its leading whole rate blocks, up to <see cref="RetainedBlocks"/> of them, in <paramref name="retained"/>.</summary>
    /// <param name="node">The node, of a length <see cref="IsResumable"/> accepts.</param>
    /// <param name="retained">Lane 0 of <see cref="RetainedBlocks"/> consecutive states.</param>
    [SkipLocalsInit]
    private static unsafe void ComputeHashRetaining(byte[] node, ref ulong retained, ref ValueHash256 hash)
    {
        Unsafe.SkipInit(out KeccakState stateBuffer);
        ref ulong lane = ref Unsafe.As<KeccakState, ulong>(ref stateBuffer);
        nuint length = (nuint)node.Length;

        fixed (byte* data = node)
        {
            ZeroCapacity(ref retained);
            AbsorbBlock(ref retained, data, intoZeroState: true);
            Accelerators.KeccakF(ref retained);
            ref ulong state = ref retained;
            nuint offset = HASH_DATA_AREA;
            for (nuint kept = 1; kept < RetainedBlocks && length - offset >= HASH_DATA_AREA; kept++)
            {
                ref ulong next = ref Unsafe.Add(ref state, STATE_LANES);
                AbsorbBlockFrom(ref next, ref state, data + offset);
                Accelerators.KeccakF(ref next);
                state = ref next;
                offset += HASH_DATA_AREA;
            }

            AbsorbRestFrom(ref lane, ref state, data + offset, length - offset);
        }

        Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) = Unsafe.Add(ref lane, HASH_DATA_AREA / sizeof(ulong) - 1) ^ (0x80UL << 56);
        Accelerators.KeccakF(ref lane);
        hash = Unsafe.As<ulong, ValueHash256>(ref lane);
    }

    /// <summary>Writes into <paramref name="lane"/> the state <paramref name="source"/> becomes once the rest of a message,
    /// the <paramref name="length"/> bytes at <paramref name="data"/>, and its 0x01 pad byte are absorbed.</summary>
    /// <remarks>A whole block must precede <paramref name="data"/>, as <see cref="AbsorbPaddedTail"/> requires.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbRestFrom(ref ulong lane, ref ulong source, byte* data, nuint length)
    {
        if (length < HASH_DATA_AREA)
        {
            CopyState(ref lane, ref source);
        }
        else
        {
            AbsorbBlockFrom(ref lane, ref source, data);
            Accelerators.KeccakF(ref lane);
            for (data += HASH_DATA_AREA, length -= HASH_DATA_AREA; length >= HASH_DATA_AREA; data += HASH_DATA_AREA, length -= HASH_DATA_AREA)
            {
                AbsorbBlock(ref lane, data, intoZeroState: false);
                Accelerators.KeccakF(ref lane);
            }
        }

        AbsorbPaddedTail(ref lane, data, length);
    }

    /// <summary>Whether the rate blocks at <paramref name="a"/> and <paramref name="b"/> are equal.</summary>
    /// <remarks>Where <see cref="ZiskFlag"/> is on this is the zkVM's DMA <c>memcmp</c>, a few steps where the lane
    /// compares take fifty-one. Elsewhere it goes lane by lane, unrolled: corelib's <c>SequenceEqual</c> has no vector
    /// width on riscv64 and pays a loop and a call for the same word compares.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool BlockEquals(byte* a, byte* b) => ZiskFlag.IsActive
        ? Accelerators.Memcmp(a, b, HASH_DATA_AREA) == 0
        : LaneEquals(a, b, 0) && LaneEquals(a, b, 1) && LaneEquals(a, b, 2) && LaneEquals(a, b, 3)
        && LaneEquals(a, b, 4) && LaneEquals(a, b, 5) && LaneEquals(a, b, 6) && LaneEquals(a, b, 7)
        && LaneEquals(a, b, 8) && LaneEquals(a, b, 9) && LaneEquals(a, b, 10) && LaneEquals(a, b, 11)
        && LaneEquals(a, b, 12) && LaneEquals(a, b, 13) && LaneEquals(a, b, 14) && LaneEquals(a, b, 15)
        && LaneEquals(a, b, 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool LaneEquals(byte* a, byte* b, nuint index) =>
        ReadLane(a, index) == ReadLane(b, index);

    /// <remarks>Where <see cref="ZiskFlag"/> is on this is one DMA <c>memset</c> rather than eight stores.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ZeroCapacity(ref ulong lane)
    {
        if (ZiskFlag.IsActive)
        {
            Bytes.Memset(Unsafe.AsPointer(ref Unsafe.Add(ref lane, RateLanes)), 0, CapacityBytes);
            return;
        }

        Unsafe.Add(ref lane, 17) = 0;
        Unsafe.Add(ref lane, 18) = 0;
        Unsafe.Add(ref lane, 19) = 0;
        Unsafe.Add(ref lane, 20) = 0;
        Unsafe.Add(ref lane, 21) = 0;
        Unsafe.Add(ref lane, 22) = 0;
        Unsafe.Add(ref lane, 23) = 0;
        Unsafe.Add(ref lane, 24) = 0;
    }

    /// <summary>Writes into <paramref name="lane"/> the state <paramref name="source"/> becomes once a whole rate block of <paramref name="data"/> is absorbed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbBlockFrom(ref ulong lane, ref ulong source, byte* data)
    {
        if (CanXorin(data))
        {
            CopyState(ref lane, ref source);
            Xorin(ref lane, data, HASH_DATA_AREA);
            return;
        }

        AbsorbLaneFrom(ref lane, ref source, data, 0);
        AbsorbLaneFrom(ref lane, ref source, data, 1);
        AbsorbLaneFrom(ref lane, ref source, data, 2);
        AbsorbLaneFrom(ref lane, ref source, data, 3);
        AbsorbLaneFrom(ref lane, ref source, data, 4);
        AbsorbLaneFrom(ref lane, ref source, data, 5);
        AbsorbLaneFrom(ref lane, ref source, data, 6);
        AbsorbLaneFrom(ref lane, ref source, data, 7);
        AbsorbLaneFrom(ref lane, ref source, data, 8);
        AbsorbLaneFrom(ref lane, ref source, data, 9);
        AbsorbLaneFrom(ref lane, ref source, data, 10);
        AbsorbLaneFrom(ref lane, ref source, data, 11);
        AbsorbLaneFrom(ref lane, ref source, data, 12);
        AbsorbLaneFrom(ref lane, ref source, data, 13);
        AbsorbLaneFrom(ref lane, ref source, data, 14);
        AbsorbLaneFrom(ref lane, ref source, data, 15);
        AbsorbLaneFrom(ref lane, ref source, data, 16);
        CopyCapacity(ref lane, ref source);
    }

    /// <summary>Writes into <paramref name="lane"/> the state <paramref name="source"/> becomes once a full branch's
    /// last <see cref="FullBranchTailLength"/> bytes and their 0x01 pad byte are absorbed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbFullBranchTailFrom(ref ulong lane, ref ulong source, byte* tail)
    {
        if (CanXorin(tail))
        {
            CopyState(ref lane, ref source);
            Xorin(ref lane, tail, FullBranchTailLength & ~7);
            Unsafe.Add(ref lane, 15) ^= PaddedLastWord(tail + FullBranchTailLength, FullBranchTailLength & 7);
            return;
        }

        AbsorbLaneFrom(ref lane, ref source, tail, 0);
        AbsorbLaneFrom(ref lane, ref source, tail, 1);
        AbsorbLaneFrom(ref lane, ref source, tail, 2);
        AbsorbLaneFrom(ref lane, ref source, tail, 3);
        AbsorbLaneFrom(ref lane, ref source, tail, 4);
        AbsorbLaneFrom(ref lane, ref source, tail, 5);
        AbsorbLaneFrom(ref lane, ref source, tail, 6);
        AbsorbLaneFrom(ref lane, ref source, tail, 7);
        AbsorbLaneFrom(ref lane, ref source, tail, 8);
        AbsorbLaneFrom(ref lane, ref source, tail, 9);
        AbsorbLaneFrom(ref lane, ref source, tail, 10);
        AbsorbLaneFrom(ref lane, ref source, tail, 11);
        AbsorbLaneFrom(ref lane, ref source, tail, 12);
        AbsorbLaneFrom(ref lane, ref source, tail, 13);
        AbsorbLaneFrom(ref lane, ref source, tail, 14);
        Unsafe.Add(ref lane, 15) = Unsafe.Add(ref source, 15) ^ PaddedLastWord(tail + FullBranchTailLength, FullBranchTailLength & 7);
        Unsafe.Add(ref lane, 16) = Unsafe.Add(ref source, 16);
        CopyCapacity(ref lane, ref source);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbLaneFrom(ref ulong lane, ref ulong source, byte* data, nuint index) =>
        Unsafe.Add(ref lane, index) = Unsafe.Add(ref source, index) ^ ReadLane(data, index);

    /// <remarks>Where <see cref="ZiskFlag"/> is on this is one DMA <c>memmove</c> rather than eight loads and stores.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void CopyCapacity(ref ulong lane, ref ulong source)
    {
        if (ZiskFlag.IsActive)
        {
            Bytes.Memmove(Unsafe.AsPointer(ref Unsafe.Add(ref lane, RateLanes)), Unsafe.AsPointer(ref Unsafe.Add(ref source, RateLanes)), CapacityBytes);
            return;
        }

        Unsafe.Add(ref lane, 17) = Unsafe.Add(ref source, 17);
        Unsafe.Add(ref lane, 18) = Unsafe.Add(ref source, 18);
        Unsafe.Add(ref lane, 19) = Unsafe.Add(ref source, 19);
        Unsafe.Add(ref lane, 20) = Unsafe.Add(ref source, 20);
        Unsafe.Add(ref lane, 21) = Unsafe.Add(ref source, 21);
        Unsafe.Add(ref lane, 22) = Unsafe.Add(ref source, 22);
        Unsafe.Add(ref lane, 23) = Unsafe.Add(ref source, 23);
        Unsafe.Add(ref lane, 24) = Unsafe.Add(ref source, 24);
    }

    /// <summary>Zeroes a state and writes a sub-rate message and its 0x01 pad byte into it.</summary>
    /// <param name="length">A constant from 8 to 135: each lane up to the padded last word then folds to one
    /// store, and the lanes past it to nothing.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe void AbsorbShortFixed(ref ulong lane, byte* data, nuint length)
    {
        ZeroState(ref lane);
        Unsafe.Add(ref lane, 0) = ShortMessageLane(data, length, 0);
        if (1 <= length >> 3) Unsafe.Add(ref lane, 1) = ShortMessageLane(data, length, 1);
        if (2 <= length >> 3) Unsafe.Add(ref lane, 2) = ShortMessageLane(data, length, 2);
        if (3 <= length >> 3) Unsafe.Add(ref lane, 3) = ShortMessageLane(data, length, 3);
        if (4 <= length >> 3) Unsafe.Add(ref lane, 4) = ShortMessageLane(data, length, 4);
        if (5 <= length >> 3) Unsafe.Add(ref lane, 5) = ShortMessageLane(data, length, 5);
        if (6 <= length >> 3) Unsafe.Add(ref lane, 6) = ShortMessageLane(data, length, 6);
        if (7 <= length >> 3) Unsafe.Add(ref lane, 7) = ShortMessageLane(data, length, 7);
        if (8 <= length >> 3) Unsafe.Add(ref lane, 8) = ShortMessageLane(data, length, 8);
        if (9 <= length >> 3) Unsafe.Add(ref lane, 9) = ShortMessageLane(data, length, 9);
        if (10 <= length >> 3) Unsafe.Add(ref lane, 10) = ShortMessageLane(data, length, 10);
        if (11 <= length >> 3) Unsafe.Add(ref lane, 11) = ShortMessageLane(data, length, 11);
        if (12 <= length >> 3) Unsafe.Add(ref lane, 12) = ShortMessageLane(data, length, 12);
        if (13 <= length >> 3) Unsafe.Add(ref lane, 13) = ShortMessageLane(data, length, 13);
        if (14 <= length >> 3) Unsafe.Add(ref lane, 14) = ShortMessageLane(data, length, 14);
        if (15 <= length >> 3) Unsafe.Add(ref lane, 15) = ShortMessageLane(data, length, 15);
        if (16 <= length >> 3) Unsafe.Add(ref lane, 16) = ShortMessageLane(data, length, 16);
    }

    /// <summary>Rate lane <paramref name="index"/>, at most the one holding the 0x01 pad byte, of a sub-rate message
    /// of at least eight bytes followed by that pad byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong ShortMessageLane(byte* data, nuint length, nuint index) =>
        index < length >> 3 ? ReadLane(data, index)
        : PaddedLastWord(data + length, length & 7);

    /// <summary>Zeroes all twenty-five lanes of a state.</summary>
    /// <remarks>Where <see cref="ZiskFlag"/> is on this is one DMA <c>memset</c> rather than twenty-five stores.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ZeroState(ref ulong lane)
    {
        if (ZiskFlag.IsActive)
        {
            Bytes.Memset(Unsafe.AsPointer(ref lane), 0, STATE_LANES * sizeof(ulong));
            return;
        }

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
        ZeroCapacity(ref lane);
    }

    /// <summary>XORs a sub-rate tail of <paramref name="length"/> bytes and the 0x01 pad byte after it into the state.</summary>
    /// <remarks>A whole block must precede the tail, which keeps the word ending the message in bounds.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbPaddedTail(ref ulong lane, byte* tail, nuint length)
    {
        if (CanXorin(tail))
        {
            Xorin(ref lane, tail, length & ~(nuint)7);
        }
        else
        {
            AbsorbLanes(ref lane, tail, length >> 3, intoZeroState: false);
        }

        ref ulong last = ref Unsafe.Add(ref lane, length >> 3);
        last = last ^ PaddedLastWord(tail + length, length & 7);
    }

    /// <summary>Absorbs the first rate block of <paramref name="data"/> into a state whose rate lanes are uninitialized.</summary>
    /// <remarks>Into an all-zero state an XOR is a copy, so where <see cref="ZiskFlag"/> is on the block goes
    /// through the zkVM's DMA <c>memmove</c>: one call instead of seventeen load/store pairs.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void CopyFirstBlock(ref ulong lane, byte* data)
    {
        if (ZiskFlag.IsActive)
        {
            Bytes.Memmove(Unsafe.AsPointer(ref lane), data, HASH_DATA_AREA);
            return;
        }

        AbsorbBlock(ref lane, data, intoZeroState: true);
    }

    /// <summary>Absorbs a whole rate block of <paramref name="data"/>.</summary>
    /// <param name="intoZeroState">Whether the state's rate lanes are still zero, so the block is written rather than XORed.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsorbBlock(ref ulong lane, byte* data, bool intoZeroState)
    {
        if (intoZeroState)
        {
            // A whole rate block written into zero lanes is a plain 136-byte copy: past the inline copy
            // threshold the guest's Memmove is the ZisK DMA idiom (a few executed instructions), where
            // seventeen explicit lane writes cost a load and a store each.
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<ulong, byte>(ref lane), ref *data, HASH_DATA_AREA);
            return;
        }

        if (CanXorin(data))
        {
            Xorin(ref lane, data, HASH_DATA_AREA);
            return;
        }

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

    /// <summary>Whether <paramref name="data"/> can be absorbed with OpenVM's XORIN instruction, which takes only 8-byte aligned operands.</summary>
    /// <remarks>
    /// Every state is ulong-aligned, being a stack <see cref="KeccakState"/> of ulong lanes or a <see cref="NativeMemory"/> witness state;
    /// a caller-chosen input often is not, and is absorbed lane by lane.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool CanXorin(byte* data) => OpenVmFlag.IsActive && ((nuint)data & 7) == 0;

    /// <summary>XORs <paramref name="length"/> bytes of <paramref name="data"/>, a multiple of 8 up to the rate, into the state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Xorin(ref ulong lane, byte* data, nuint length)
    {
        if (length != 0)
            Accelerators.KeccakXorin((ulong*)Unsafe.AsPointer(ref lane), data, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyState(ref ulong lane, ref ulong source) =>
        Unsafe.As<ulong, KeccakState>(ref lane) = Unsafe.As<ulong, KeccakState>(ref source);

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
        ulong word = ReadLane(data, index);
        Unsafe.Add(ref lane, index) = intoZeroState ? word : Unsafe.Add(ref lane, index) ^ word;
    }

    /// <summary>Lane <paramref name="index"/> of <paramref name="data"/>, which need not be aligned.</summary>
    /// <remarks>A plain load rather than an unaligned read: every guest target executes either as one wide load, but where
    /// the JIT tests alignment (<see cref="StrictAlignmentFlag"/>) it drops the test for a plain load whose address it can
    /// prove aligned, and keeps it for an unaligned read always.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong ReadLane(byte* data, nuint index) =>
        *(ulong*)(data + index * sizeof(ulong));

    /// <summary><paramref name="pointer"/> rounded down to an 8-byte boundary: itself, for a pointer already on one.</summary>
    /// <remarks>The JIT sees through the rounding (dotnet-riscv perf-67) and proves every lane access off the result aligned.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe T* AlignedDown<T>(T* pointer) where T : unmanaged => (T*)((nuint)pointer & ~(nuint)7);

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

        // See AbsorbBlock: one DMA-backed copy instead of seventeen lane writes.
        Unsafe.CopyBlockUnaligned(ref MemoryMarshal.GetReference(state), ref MemoryMarshal.GetReference(block), HASH_DATA_AREA);
    }
}
