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
    /// falls back to <see cref="XorVectors"/> — into an all-zero state, an XOR is a write.</remarks>
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
