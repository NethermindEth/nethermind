// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.TwoByteSlot;

/// <summary>
/// Read-side helpers for the keys-first TwoByteSlot value layouts —
/// <see cref="IndexType.TwoByteSlotValue"/> (u16 offsets) and
/// <see cref="IndexType.TwoByteSlotValueLarge"/> (u24 offsets). The on-disk offset width
/// is the only difference between them; the caller threads it in as <c>offsetSize</c>
/// after dispatching on the leading <see cref="IndexType"/> byte. Stateless static methods
/// so <see cref="HsstReader{TReader,TPin}"/> and <see cref="HsstEnumerator{TReader,TPin}"/>
/// can dispatch into them without copying their ref-struct state.
///
/// Wire shape (keys-first):
/// <c>[IndexType: u8][KeyCount: u16 LE][Keys: N·2][Offsets: (N-1)·offsetSize][Values]</c>.
/// </summary>
internal static class HsstTwoByteSlotValueReader
{
    public const int KeyLength = HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.KeyLength;

    /// <summary>Parsed header of a TwoByteSlot value HSST.</summary>
    internal struct Layout
    {
        /// <summary>Number of entries (N; Offset_0 is implicit zero).</summary>
        public int Count;
        /// <summary>On-disk width in bytes of each explicit offset (2 or 3).</summary>
        public int OffsetSize;
        /// <summary>Absolute offset of the keys array (<c>Count · 2</c> bytes).</summary>
        public long KeysStart;
        /// <summary>Absolute offset of the explicit offsets array (<c>(Count − 1) · OffsetSize</c> bytes).</summary>
        public long OffsetsStart;
        /// <summary>Absolute offset of the values section (byte after offsets).</summary>
        public long ValuesStart;
        /// <summary>Absolute one-past-end of the values section (= the blob's end).</summary>
        public long ValuesEnd;
    }

    /// <summary>
    /// Parse the TwoByteSlot value header. Returns false on truncation or invalid count.
    /// Caller must have already dispatched on the leading <see cref="IndexType"/> byte
    /// (byte 0 of <paramref name="bound"/>) and supply the matching <paramref name="offsetSize"/>
    /// (2 for <see cref="IndexType.TwoByteSlotValue"/>, 3 for <see cref="IndexType.TwoByteSlotValueLarge"/>).
    /// </summary>
    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, int offsetSize, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        // Smallest valid HSST: 1 entry with empty value = 1 (type) + 2 (count) + 2 (key) + 0 (offsets) + 0 (values) = 5 bytes.
        if (bound.Length < 5) return false;

        // KeyCount sits right after the leading IndexType byte.
        ushort countLE = 0;
        if (!reader.TryRead(bound.Offset + 1, MemoryMarshal.AsBytes(new Span<ushort>(ref countLE)))) return false;
        int count = countLE + 1;

        // IndexType + KeyCount + keys + offsets; reject if it exceeds the blob.
        long overhead = 3L + (long)KeyLength * count + (long)offsetSize * (count - 1);
        if (overhead > bound.Length) return false;

        long keysStart = bound.Offset + 3;
        long offsetsStart = keysStart + (long)count * KeyLength;
        long valuesStart = offsetsStart + (long)(count - 1) * offsetSize;
        long valuesEnd = bound.Offset + bound.Length;

        layout.Count = count;
        layout.OffsetSize = offsetSize;
        layout.KeysStart = keysStart;
        layout.OffsetsStart = offsetsStart;
        layout.ValuesStart = valuesStart;
        layout.ValuesEnd = valuesEnd;
        return true;
    }

    /// <summary>
    /// Exact-match or floor lookup over a TwoByteSlot value HSST. <paramref name="key"/>
    /// must be exactly 2 bytes (any other length rejects). Floor semantics: largest
    /// stored key ≤ target. Zero-length values are legal and round-trip as empty bounds.
    /// </summary>
    public static bool TrySeek<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key,
        bool exactMatch, int offsetSize, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;
        if (key.Length != KeyLength) return false;
        if (!TryReadLayout<TReader, TPin>(in reader, bound, offsetSize, out Layout L)) return false;

        long keysBytes = (long)L.Count * KeyLength;
        using TPin keysPin = reader.PinBuffer(new Bound(L.KeysStart, keysBytes));
        ReadOnlySpan<byte> keys = keysPin.Buffer;

        int idx = UniformKeySearch.LowerBound2LE(keys, L.Count, key);
        bool exact;
        if (idx < L.Count)
        {
            // Keys are LE-stored: native u16 load recovers the BE numeric value.
            // Compare against the target's BE numeric value derived the same way.
            ushort storedBeValue = UniformKeySearch.ReadKey2LE(keys, idx);
            ushort targetBeValue = BinaryPrimitives.ReadUInt16BigEndian(key);
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
    /// in <c>[0, Count)</c>. Reads the entry's start and end from the offsets array.
    /// Caller pre-validates index range.
    /// </summary>
    public static bool TryResolve<TReader, TPin>(scoped in TReader reader, in Layout L, int idx, out Bound entryBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        entryBound = default;
        long start = idx == 0 ? 0L : ReadOffsetLE<TReader, TPin>(in reader, L.OffsetsStart + (long)(idx - 1) * L.OffsetSize, L.OffsetSize);
        long end = idx == L.Count - 1
            ? L.ValuesEnd - L.ValuesStart
            : ReadOffsetLE<TReader, TPin>(in reader, L.OffsetsStart + (long)idx * L.OffsetSize, L.OffsetSize);
        if (end < start) return false;
        entryBound = new Bound(L.ValuesStart + start, end - start);
        return true;
    }

    /// <summary>Read a <paramref name="size"/>-byte (2 or 3) little-endian offset. Returns -1 on read failure.</summary>
    internal static long ReadOffsetLE<TReader, TPin>(scoped in TReader reader, long offset, int size)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        uint value = 0;
        Span<byte> buf = MemoryMarshal.AsBytes(new Span<uint>(ref value));
        if (!reader.TryRead(offset, buf[..size])) return -1;
        return value;
    }
}
