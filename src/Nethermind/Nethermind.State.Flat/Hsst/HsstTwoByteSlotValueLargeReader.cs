// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.State.Flat.BSearchIndex;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.TwoByteSlotValueLarge"/> layout —
/// the u24-offset sibling of <see cref="HsstTwoByteSlotValueReader"/>. Stateless
/// static methods so <see cref="HsstReader{TReader,TPin}"/> and
/// <see cref="HsstEnumerator{TReader,TPin}"/> can dispatch into them without copying
/// their ref-struct state.
///
/// Wire shape (keys-first):
/// <c>[KeyCount: u16 LE][Keys: N·2][Offsets: (N-1)·3][Values][IndexType: u8]</c>.
/// </summary>
internal static class HsstTwoByteSlotValueLargeReader
{
    public const int KeyLength = HsstTwoByteSlotValueLargeBuilder<SpanBufferWriter>.KeyLength;
    public const int OffsetSize = HsstTwoByteSlotValueLargeBuilder<SpanBufferWriter>.OffsetSize;

    /// <summary>Parsed header of a TwoByteSlotValueLarge HSST.</summary>
    internal struct Layout
    {
        /// <summary>Number of entries (N; Offset_0 is implicit zero).</summary>
        public int Count;
        /// <summary>Absolute offset of the keys array (<c>Count · 2</c> bytes).</summary>
        public long KeysStart;
        /// <summary>Absolute offset of the explicit offsets array (<c>(Count − 1) · 3</c> bytes).</summary>
        public long OffsetsStart;
        /// <summary>Absolute offset of the values section (byte after offsets).</summary>
        public long ValuesStart;
        /// <summary>Absolute one-past-end of the values section (= byte before <see cref="IndexType"/>).</summary>
        public long ValuesEnd;
    }

    /// <summary>
    /// Parse the TwoByteSlotValueLarge header. Returns false on truncation or invalid count.
    /// Caller must have already verified the trailing <see cref="IndexType"/> byte equals
    /// <see cref="IndexType.TwoByteSlotValueLarge"/>.
    /// </summary>
    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        // Smallest valid HSST: 1 entry with empty value = 2 (count) + 2 (key) + 0 (offsets) + 0 (values) + 1 (type) = 5 bytes.
        if (bound.Length < 5) return false;

        Span<byte> countBuf = stackalloc byte[2];
        if (!reader.TryRead(bound.Offset, countBuf)) return false;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(countBuf) + 1;

        // Header + keys + offsets + IndexType = 5N; reject if it exceeds the blob.
        long overhead = 5L * count;
        if (overhead > bound.Length) return false;

        long keysStart = bound.Offset + 2;
        long offsetsStart = keysStart + (long)count * KeyLength;
        long valuesStart = offsetsStart + (long)(count - 1) * OffsetSize;
        long valuesEnd = bound.Offset + bound.Length - 1;

        layout.Count = count;
        layout.KeysStart = keysStart;
        layout.OffsetsStart = offsetsStart;
        layout.ValuesStart = valuesStart;
        layout.ValuesEnd = valuesEnd;
        return true;
    }

    /// <summary>
    /// Exact-match or floor lookup over a TwoByteSlotValueLarge HSST. <paramref name="key"/>
    /// must be exactly 2 bytes (any other length rejects). Floor semantics: largest
    /// stored key ≤ target. Zero-length values are legal and round-trip as empty bounds.
    /// </summary>
    public static bool TrySeek<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key,
        bool exactMatch, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;
        if (key.Length != KeyLength) return false;
        if (!TryReadLayout<TReader, TPin>(in reader, bound, out Layout L)) return false;

        long keysBytes = (long)L.Count * KeyLength;
        using TPin keysPin = reader.PinBuffer(L.KeysStart, keysBytes);
        ReadOnlySpan<byte> keys = keysPin.Buffer;

        int idx = UniformKeySearch.LowerBound2LE(keys, L.Count, key);
        bool exact;
        if (idx < L.Count)
        {
            ushort storedBeValue = UniformKeySearch.ReadKey2LE(keys, idx);
            ushort targetBeValue = (ushort)((key[0] << 8) | key[1]);
            exact = storedBeValue == targetBeValue;
        }
        else
        {
            exact = false;
        }

        int hit;
        if (exact)
        {
            hit = idx;
        }
        else if (exactMatch)
        {
            return false;
        }
        else
        {
            if (idx == 0) return false;
            hit = idx - 1;
        }

        return TryResolve<TReader, TPin>(in reader, L, hit, out resultBound);
    }

    /// <summary>
    /// Resolve entry <paramref name="idx"/>'s value bound. <paramref name="idx"/> must be
    /// in <c>[0, Count)</c>. Reads at most 6 bytes from the offsets array (the entry's
    /// start and end). Caller pre-validates index range.
    /// </summary>
    public static bool TryResolve<TReader, TPin>(scoped in TReader reader, in Layout L, int idx, out Bound entryBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        entryBound = default;
        long start = idx == 0 ? 0L : ReadU24LE<TReader, TPin>(in reader, L.OffsetsStart + (long)(idx - 1) * OffsetSize);
        long end = idx == L.Count - 1
            ? L.ValuesEnd - L.ValuesStart
            : ReadU24LE<TReader, TPin>(in reader, L.OffsetsStart + (long)idx * OffsetSize);
        if (end < start) return false;
        entryBound = new Bound(L.ValuesStart + start, end - start);
        return true;
    }

    /// <summary>Resolve all entry bounds into <paramref name="dst"/>. Returns Count or 0 if dst is too small.</summary>
    public static int TryResolveAll<TReader, TPin>(scoped in TReader reader, Bound bound, Span<Bound> dst)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        if (!TryReadLayout<TReader, TPin>(in reader, bound, out Layout L)) return 0;
        if (L.Count > dst.Length) return 0;
        if (L.Count == 1)
        {
            dst[0] = new Bound(L.ValuesStart, L.ValuesEnd - L.ValuesStart);
            return 1;
        }

        long offsetsBytes = (long)(L.Count - 1) * OffsetSize;
        using TPin offsetsPin = reader.PinBuffer(L.OffsetsStart, offsetsBytes);
        ReadOnlySpan<byte> offsets = offsetsPin.Buffer;

        long prevStart = 0;
        Span<byte> scratch = stackalloc byte[4];
        for (int i = 0; i < L.Count - 1; i++)
        {
            scratch.Clear();
            offsets.Slice(i * OffsetSize, OffsetSize).CopyTo(scratch);
            long nextStart = BinaryPrimitives.ReadUInt32LittleEndian(scratch);
            dst[i] = new Bound(L.ValuesStart + prevStart, nextStart - prevStart);
            prevStart = nextStart;
        }
        dst[L.Count - 1] = new Bound(L.ValuesStart + prevStart, L.ValuesEnd - L.ValuesStart - prevStart);
        return L.Count;
    }

    internal static long ReadU24LE<TReader, TPin>(scoped in TReader reader, long offset)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        Span<byte> buf = stackalloc byte[4];
        buf[3] = 0;
        if (!reader.TryRead(offset, buf[..3])) return -1;
        return BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }
}
