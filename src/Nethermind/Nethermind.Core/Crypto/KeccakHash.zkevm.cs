// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void KeccakF(Span<ulong> st)
    {
        Debug.Assert(st.Length == STATE_LANES);
        SyscallKeccakF(ref MemoryMarshal.GetReference(st));
    }

    /// <summary>The zkVM's Keccak-f[1600] precompile, applied in place to the 25 lanes at <paramref name="state"/>.</summary>
    /// <remarks>The entry point <c>Accelerators.KeccakF</c> binds to, imported here so that it can suppress the
    /// GC transition: the precompile can neither block nor call back, while a transitioning P/Invoke makes its
    /// caller save every callee-saved register and spill its live GC refs around each permutation.</remarks>
    [LibraryImport("__Internal", EntryPoint = "syscall_keccak_f")]
    [SuppressGCTransition]
    private static partial void SyscallKeccakF(ref ulong state);

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
