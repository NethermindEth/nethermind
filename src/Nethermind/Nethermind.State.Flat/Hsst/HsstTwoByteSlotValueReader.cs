// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.TwoByteSlotValue"/> layout.
/// Stateless static methods so <see cref="HsstReader{TReader,TPin}"/> and
/// <see cref="HsstEnumerator{TReader,TPin}"/> can dispatch into them without copying
/// their ref-struct state.
/// </summary>
internal static class HsstTwoByteSlotValueReader
{
    public const int KeyLength = HsstTwoByteSlotValueBuilder<SpanBufferWriter>.KeyLength;
    private const int OffsetSize = 2;

    /// <summary>Parsed footer of a TwoByteSlotValue HSST.</summary>
    internal struct Layout
    {
        /// <summary>Absolute offset of byte 0 of the HSST (= start of the value region).</summary>
        public long DataStart;
        /// <summary>Number of entries (N; Offset_0 is implicit zero).</summary>
        public int Count;
        /// <summary>Absolute offset of the keys array (<c>Count · 2</c> bytes).</summary>
        public long KeysStart;
        /// <summary>Absolute offset of the explicit offsets array (<c>(Count − 1) · 2</c> bytes).</summary>
        public long OffsetsStart;
        /// <summary>Absolute one-past-end of the data region (= start of offsets section).</summary>
        public long DataEnd;
    }

    /// <summary>
    /// Parse the TwoByteSlotValue trailer. Returns false on truncation or invalid count.
    /// Caller must have already verified the trailing <see cref="IndexType"/> byte equals
    /// <see cref="IndexType.TwoByteSlotValue"/>.
    /// </summary>
    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        // Smallest valid HSST: 1 entry with empty value = 0 (data) + 0 (offsets) + 2 (key) + 2 (count) + 1 (type) = 5 bytes.
        if (bound.Length < 5) return false;

        Span<byte> countBuf = stackalloc byte[2];
        if (!reader.TryRead(bound.Offset + bound.Length - 3, countBuf)) return false;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(countBuf) + 1;

        long trailerLen = 4L * count + 1L;
        if (trailerLen > bound.Length) return false;

        long keysStart = bound.Offset + bound.Length - 3 - (long)count * KeyLength;
        long offsetsStart = keysStart - (long)(count - 1) * OffsetSize;

        layout.DataStart = bound.Offset;
        layout.Count = count;
        layout.KeysStart = keysStart;
        layout.OffsetsStart = offsetsStart;
        layout.DataEnd = offsetsStart;
        return true;
    }

    /// <summary>
    /// Exact-match or floor lookup over a TwoByteSlotValue HSST. <paramref name="key"/>
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

        int idx = HsstTwoByteKeySearch.LowerBoundLeStored(keys, L.Count, key);
        bool exact;
        if (idx < L.Count)
        {
            // Keys are LE-stored: native u16 load recovers the BE numeric value.
            // Compare against the target's BE numeric value derived the same way.
            ushort storedBeValue = HsstTwoByteKeySearch.ReadKeyAt(keys, idx);
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
            // Floor: predecessor. idx is the insertion point of `key` in the sorted
            // keys array; the floor entry sits at idx - 1.
            if (idx == 0) return false;
            hit = idx - 1;
        }

        return TryResolve<TReader, TPin>(in reader, L, hit, out resultBound);
    }

    /// <summary>
    /// Resolve entry <paramref name="idx"/>'s value bound. <paramref name="idx"/> must be
    /// in <c>[0, Count)</c>. Reads at most 4 bytes from the offsets array (the entry's
    /// start and end). Caller pre-validates index range.
    /// </summary>
    public static bool TryResolve<TReader, TPin>(scoped in TReader reader, in Layout L, int idx, out Bound entryBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        entryBound = default;
        long start = idx == 0 ? 0L : ReadU16LE<TReader, TPin>(in reader, L.OffsetsStart + (long)(idx - 1) * OffsetSize);
        long end = idx == L.Count - 1
            ? L.DataEnd - L.DataStart
            : ReadU16LE<TReader, TPin>(in reader, L.OffsetsStart + (long)idx * OffsetSize);
        if (end < start) return false;
        entryBound = new Bound(L.DataStart + start, end - start);
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
            dst[0] = new Bound(L.DataStart, L.DataEnd - L.DataStart);
            return 1;
        }

        long offsetsBytes = (long)(L.Count - 1) * OffsetSize;
        using TPin offsetsPin = reader.PinBuffer(L.OffsetsStart, offsetsBytes);
        ReadOnlySpan<byte> offsets = offsetsPin.Buffer;

        long prevStart = 0;
        for (int i = 0; i < L.Count - 1; i++)
        {
            long nextStart = BinaryPrimitives.ReadUInt16LittleEndian(offsets[(i * OffsetSize)..]);
            dst[i] = new Bound(L.DataStart + prevStart, nextStart - prevStart);
            prevStart = nextStart;
        }
        dst[L.Count - 1] = new Bound(L.DataStart + prevStart, L.DataEnd - L.DataStart - prevStart);
        return L.Count;
    }

    private static long ReadU16LE<TReader, TPin>(scoped in TReader reader, long offset)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        Span<byte> buf = stackalloc byte[2];
        if (!reader.TryRead(offset, buf)) return -1;
        return BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }
}
