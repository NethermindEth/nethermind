// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    /// <summary>Writes <paramref name="value"/> as one big-endian 32-byte stack word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteBeWord(ref EvmWord head, in UInt256 value)
    {
        ulong u3 = Bytes.Bswap64(value.u3);
        ulong u2 = Bytes.Bswap64(value.u2);
        ulong u1 = Bytes.Bswap64(value.u1);
        ulong u0 = Bytes.Bswap64(value.u0);

        ref byte destination = ref Unsafe.As<EvmWord, byte>(ref head);
        Unsafe.WriteUnaligned(ref destination, u3);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, sizeof(ulong)), u2);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 2 * sizeof(ulong)), u1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 3 * sizeof(ulong)), u0);
    }

    /// <summary>Reads one big-endian 32-byte stack word into a <see cref="UInt256"/>.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt256 ReadBeWord(ref byte bytes)
    {
        // Combine read and switch endianness to movbe reg, mem
        ulong u3 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref bytes));
        ulong u2 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, sizeof(ulong))));
        ulong u1 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 2 * sizeof(ulong))));
        ulong u0 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 3 * sizeof(ulong))));

        return new UInt256(u0, u1, u2, u3);
    }

    /// <inheritdoc cref="ReadBeWords(ref byte, out UInt256, out UInt256, out UInt256, out UInt256)"/>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadBeWords(ref byte bytes, out UInt256 a, out UInt256 b)
    {
        // Interleave loads across both values
        ulong b3 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref bytes));
        ulong a3 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 32)));

        ulong b2 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 8)));
        ulong a2 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 40)));

        ulong b1 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 16)));
        ulong a1 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 48)));

        ulong b0 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 24)));
        ulong a0 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 56)));

        b = new UInt256(b0, b1, b2, b3);
        a = new UInt256(a0, a1, a2, a3);
    }

    /// <inheritdoc cref="ReadBeWords(ref byte, out UInt256, out UInt256, out UInt256, out UInt256)"/>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadBeWords(ref byte bytes, out UInt256 a, out UInt256 b, out UInt256 c)
    {
        // Round 1: high qwords (u3) from each value
        ulong c3 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref bytes));
        ulong b3 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 32)));
        ulong a3 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 64)));

        // Round 2: u2 from each value
        ulong c2 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 8)));
        ulong b2 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 40)));
        ulong a2 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 72)));

        // Round 3: u1 from each value
        ulong c1 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 16)));
        ulong b1 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 48)));
        ulong a1 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 80)));

        // Round 4: low qwords (u0) from each value
        ulong c0 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 24)));
        ulong b0 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 56)));
        ulong a0 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 88)));

        c = new UInt256(c0, c1, c2, c3);
        b = new UInt256(b0, b1, b2, b3);
        a = new UInt256(a0, a1, a2, a3);
    }

    /// <summary>Reads adjacent big-endian stack words, <paramref name="a"/> being the top of the stack.</summary>
    /// <remarks>
    /// Loads are interleaved across the words to break dependency chains and hide load-to-use
    /// latency; modern CPUs can have 10+ loads in flight simultaneously. The words sit deepest
    /// first, so <paramref name="bytes"/> addresses the last parameter and the top of the stack
    /// is the highest offset.
    /// </remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadBeWords(ref byte bytes, out UInt256 a, out UInt256 b, out UInt256 c, out UInt256 d)
    {
        // Round 1: high qwords (u3)
        ulong d3 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref bytes));
        ulong c3 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 32)));
        ulong b3 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 64)));
        ulong a3 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 96)));

        // Round 2: u2
        ulong d2 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 8)));
        ulong c2 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 40)));
        ulong b2 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 72)));
        ulong a2 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 104)));

        // Round 3: u1
        ulong d1 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 16)));
        ulong c1 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 48)));
        ulong b1 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 80)));
        ulong a1 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 112)));

        // Round 4: low qwords (u0)
        ulong d0 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 24)));
        ulong c0 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 56)));
        ulong b0 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 88)));
        ulong a0 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 120)));

        d = new UInt256(d0, d1, d2, d3);
        c = new UInt256(c0, c1, c2, c3);
        b = new UInt256(b0, b1, b2, b3);
        a = new UInt256(a0, a1, a2, a3);
    }
}
