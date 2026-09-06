// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void KeccakF(Span<ulong> st) => Accelerators.KeccakF(st);

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
