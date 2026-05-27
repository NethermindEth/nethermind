// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.TwoByteSlot;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.TwoByteSlotValueLarge"/> layout —
/// the u24-offset sibling of <see cref="HsstTwoByteSlotValueReader"/>. Stateless
/// static methods so <see cref="HsstReader{TReader,TPin}"/> and
/// <see cref="HsstEnumerator{TReader,TPin}"/> can dispatch into them without copying
/// their ref-struct state.
///
/// Wire shape (keys-first):
/// <c>[IndexType: u8][KeyCount: u16 LE][Keys: N·2][Offsets: (N-1)·3][Values]</c>.
/// </summary>
internal static class HsstTwoByteSlotValueLargeReader
{
    public const int KeyLength = HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer>.KeyLength;
    public const int OffsetSize = HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer>.OffsetSize;

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
        /// <summary>Absolute one-past-end of the values section (= the blob's end).</summary>
        public long ValuesEnd;
    }

    /// <summary>
    /// Parse the TwoByteSlotValueLarge header. Returns false on truncation or invalid count.
    /// Caller must have already dispatched on the leading <see cref="IndexType"/> byte
    /// (byte 0 of <paramref name="bound"/>) as <see cref="IndexType.TwoByteSlotValueLarge"/>.
    /// </summary>
    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        // Smallest valid HSST: 1 entry with empty value = 1 (type) + 2 (count) + 2 (key) + 0 (offsets) + 0 (values) = 5 bytes.
        if (bound.Length < 5) return false;

        // KeyCount sits right after the leading IndexType byte.
        Span<byte> countBuf = stackalloc byte[2];
        if (!reader.TryRead(bound.Offset + 1, countBuf)) return false;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(countBuf) + 1;

        // IndexType + header + keys + offsets = 5N; reject if it exceeds the blob.
        long overhead = 5L * count;
        if (overhead > bound.Length) return false;

        long keysStart = bound.Offset + 3;
        long offsetsStart = keysStart + (long)count * KeyLength;
        long valuesStart = offsetsStart + (long)(count - 1) * OffsetSize;
        long valuesEnd = bound.Offset + bound.Length;

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
