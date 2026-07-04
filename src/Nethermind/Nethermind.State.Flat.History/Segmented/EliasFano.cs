// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.State.Flat.History.Segmented;

/// <summary>
/// Elias-Fano encoding of a non-decreasing <see cref="ulong"/> sequence, the index half of the Approach-2 split
/// index/values layout. For an entity that changed at blocks <c>b0 - b1 - … - b(n-1)</c> the blob stores the
/// list in <c>~n·(⌈log2(u/n)⌉ + 2)</c> bits and answers <see cref="Encode"/> — the largest block ≤ a query and,
/// crucially, its <b>rank i</b>, which is the position of the matching value in the entity's packed value run.
/// </summary>
/// <remarks>
/// Layout: each value splits into its low <c>L = ⌊log2(u/n)⌋</c> bits (packed contiguously) and its high bits (a
/// bucket bitmap where element i sets bit <c>(value_i &gt;&gt; L) + i</c>, giving n ones and ≈n zeros). Reads use
/// broadword <c>select</c> over the bitmap. Blob bytes:
/// <code>
/// [0]      byte   L
/// [1]      byte   reserved (0)
/// [2..6)   u32    n           (little-endian)
/// [6..14)  u64    u = max     (little-endian; 0 when n == 0)
/// [14..)   u64[]  low words   (⌈n·L / 64⌉ words, little-endian)
/// [ …  )   u64[]  high words  (⌈(n + (u&gt;&gt;L) + 1) / 64⌉ words, little-endian)
/// </code>
/// </remarks>
public static class EliasFano
{
    private const int HeaderBytes = 14;

    /// <summary>Number of bytes <see cref="Encode"/> will write for <paramref name="values"/>.</summary>
    public static int GetEncodedLength(scoped ReadOnlySpan<ulong> values)
    {
        Layout layout = Layout.For(values);
        return HeaderBytes + (layout.LowWords + layout.HighWords) * sizeof(ulong);
    }

    /// <summary>Encodes the non-decreasing <paramref name="values"/> into <paramref name="destination"/>
    /// (sized via <see cref="GetEncodedLength"/>) and returns the number of bytes written.</summary>
    public static int Encode(scoped ReadOnlySpan<ulong> values, Span<byte> destination)
    {
        Layout layout = Layout.For(values);
        int total = HeaderBytes + (layout.LowWords + layout.HighWords) * sizeof(ulong);
        destination = destination[..total];
        destination.Clear();

        int n = values.Length;
        destination[0] = (byte)layout.L;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[2..], (uint)n);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[6..], layout.Max);

        if (n == 0) return total;

        // Word-addressed scratch, rented since sealing is a cold path and sequences can be large.
        ulong[] scratch = ArrayPool<ulong>.Shared.Rent(layout.LowWords + layout.HighWords);
        try
        {
            Span<ulong> lowWords = scratch.AsSpan(0, layout.LowWords);
            Span<ulong> highWords = scratch.AsSpan(layout.LowWords, layout.HighWords);
            lowWords.Clear();
            highWords.Clear();

            int l = layout.L;
            ulong lowMask = LowMask(l);
            for (int i = 0; i < n; i++)
            {
                ulong v = values[i];
                if (l != 0) SetBits(lowWords, (long)i * l, l, v & lowMask);
                int highPos = (int)(v >> l) + i;
                highWords[highPos >> 6] |= 1UL << (highPos & 63);
            }

            Span<byte> lowDst = destination[HeaderBytes..];
            for (int w = 0; w < layout.LowWords; w++)
                BinaryPrimitives.WriteUInt64LittleEndian(lowDst[(w * sizeof(ulong))..], lowWords[w]);

            Span<byte> highDst = lowDst[(layout.LowWords * sizeof(ulong))..];
            for (int w = 0; w < layout.HighWords; w++)
                BinaryPrimitives.WriteUInt64LittleEndian(highDst[(w * sizeof(ulong))..], highWords[w]);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(scratch);
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong LowMask(int l) => l == 0 ? 0UL : l >= 64 ? ~0UL : (1UL << l) - 1;

    // OR `value` (occupying `count` bits) into the word-addressed bit array at absolute bit position `bitPos`.
    private static void SetBits(Span<ulong> words, long bitPos, int count, ulong value)
    {
        int w = (int)(bitPos >> 6);
        int off = (int)(bitPos & 63);
        words[w] |= value << off;
        if (off + count > 64) words[w + 1] |= value >> (64 - off);
    }

    private readonly struct Layout
    {
        public readonly int L;
        public readonly int LowWords;
        public readonly int HighWords;
        public readonly ulong Max;

        private Layout(int l, int lowWords, int highWords, ulong max)
        {
            L = l;
            LowWords = lowWords;
            HighWords = highWords;
            Max = max;
        }

        public static Layout For(scoped ReadOnlySpan<ulong> values)
        {
            int n = values.Length;
            if (n == 0) return new Layout(0, 0, 0, 0);

            ulong u = values[n - 1];
            ulong ratio = u / (ulong)n;
            int l = ratio == 0 ? 0 : BitOperations.Log2(ratio);

            int lowWords = (int)(((long)n * l + 63) >> 6);
            long highBitLen = n + (long)(u >> l) + 1;
            int highWords = (int)((highBitLen + 63) >> 6);
            return new Layout(l, lowWords, highWords, u);
        }
    }

    /// <summary>Zero-allocation reader over an encoded blob (e.g. a slice of an mmap'd segment).</summary>
    public readonly ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _lowWords;
        private readonly ReadOnlySpan<byte> _highWords;
        private readonly int _n;
        private readonly int _l;
        private readonly ulong _max;
        private readonly ulong _lowMask;

        public Reader(ReadOnlySpan<byte> blob)
        {
            _l = blob[0];
            _n = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[2..]);
            _max = BinaryPrimitives.ReadUInt64LittleEndian(blob[6..]);
            _lowMask = LowMask(_l);

            int lowWords = (int)(((long)_n * _l + 63) >> 6);
            long highBitLen = _n == 0 ? 0 : _n + (long)(_max >> _l) + 1;
            int highWords = (int)((highBitLen + 63) >> 6);

            ReadOnlySpan<byte> body = blob[HeaderBytes..];
            _lowWords = body[..(lowWords * sizeof(ulong))];
            _highWords = body.Slice(lowWords * sizeof(ulong), highWords * sizeof(ulong));
        }

        public int Count => _n;

        /// <summary>The value at rank <paramref name="i"/> (0-based).</summary>
        public ulong this[int i] => ((ulong)(Select1(i) - i) << _l) | ReadLow(i);

        /// <summary>
        /// Largest value ≤ <paramref name="query"/>. Returns its <paramref name="rank"/> (position in the value run)
        /// and <paramref name="value"/>; <c>false</c> when every value is greater (no predecessor).
        /// </summary>
        public bool Predecessor(ulong query, out int rank, out ulong value)
        {
            if (_n == 0)
            {
                rank = -1;
                value = 0;
                return false;
            }

            if (query >= _max)
            {
                rank = _n - 1;
                value = _max;
                return true;
            }

            int high = (int)(query >> _l);
            ulong low = query & _lowMask;

            int countLeHigh = Select0(high) - high;               // elements with (v >> L) <= high
            int bucketStart = high == 0 ? 0 : Select0(high - 1) - (high - 1);

            // Within the target bucket, values share `high`, so compare only the low bits; scan back for the last fit.
            for (int i = countLeHigh - 1; i >= bucketStart; i--)
            {
                ulong lowI = ReadLow(i);
                if (lowI <= low)
                {
                    rank = i;
                    value = ((ulong)high << _l) | lowI;
                    return true;
                }
            }

            // Nothing in the bucket fits; the predecessor is the last element of a lower bucket, if any.
            if (bucketStart == 0)
            {
                rank = -1;
                value = 0;
                return false;
            }

            rank = bucketStart - 1;
            value = this[rank];
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ReadLow(int i)
        {
            if (_l == 0) return 0;
            long bit = (long)i * _l;
            int w = (int)(bit >> 6);
            int off = (int)(bit & 63);
            ulong lo = Word(_lowWords, w) >> off;
            if (off + _l > 64) lo |= Word(_lowWords, w + 1) << (64 - off);
            return lo & _lowMask;
        }

        // Position of the k-th set bit (0-based) in the high bitmap.
        private int Select1(int k)
        {
            int word = 0;
            int seen = 0;
            while (true)
            {
                ulong w = Word(_highWords, word);
                int pc = BitOperations.PopCount(w);
                if (seen + pc > k) return (word << 6) + SelectInWord(w, k - seen);
                seen += pc;
                word++;
            }
        }

        // Position of the k-th zero bit (0-based) in the high bitmap. Bits past the logical length never occur here
        // because callers only request zeros with index <= (max >> L), all of which lie inside the bitmap.
        private int Select0(int k)
        {
            int word = 0;
            int seen = 0;
            while (true)
            {
                ulong w = ~Word(_highWords, word);
                int pc = BitOperations.PopCount(w);
                if (seen + pc > k) return (word << 6) + SelectInWord(w, k - seen);
                seen += pc;
                word++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Word(ReadOnlySpan<byte> words, int index) =>
            BinaryPrimitives.ReadUInt64LittleEndian(words[(index * sizeof(ulong))..]);
    }

    // Index of the r-th set bit (0-based) within a single word; the word must hold more than r set bits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SelectInWord(ulong word, int r)
    {
        if (Bmi2.X64.IsSupported)
            return BitOperations.TrailingZeroCount(Bmi2.X64.ParallelBitDeposit(1UL << r, word));

        for (int c = 0; ; c++)
        {
            int tz = BitOperations.TrailingZeroCount(word);
            if (c == r) return tz;
            word &= word - 1;
        }
    }
}
