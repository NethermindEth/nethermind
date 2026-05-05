// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.ByteTagMap"/> layout. Stateless static
/// methods so <see cref="HsstReader{TReader,TPin}"/> can dispatch into them without copying
/// its ref-struct state.
/// </summary>
internal static class HsstByteTagMapReader
{
    /// <summary>Parsed footer of a ByteTagMap HSST.</summary>
    internal struct Layout
    {
        /// <summary>Absolute offset of byte 0 of the HSST (= start of the value region).</summary>
        public long DataStart;
        /// <summary>Number of entries.</summary>
        public int Count;
        /// <summary>Absolute offset of the <c>Ends</c> array (4·Count bytes).</summary>
        public long EndsStart;
        /// <summary>Absolute offset of the <c>Tags</c> array (Count bytes, adjacent to the trailer).</summary>
        public long TagsStart;
    }

    /// <summary>
    /// Parse the ByteTagMap trailer. Returns false on truncation. Caller must have already
    /// verified the trailing <see cref="IndexType"/> byte equals
    /// <see cref="IndexType.ByteTagMap"/>.
    /// </summary>
    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        if (bound.Length < 2) return false;

        Span<byte> oneByte = stackalloc byte[1];
        if (!reader.TryRead(bound.Offset + bound.Length - 2, oneByte)) return false;
        // Count byte stores N - 1; the empty map cannot be represented by this format.
        int count = oneByte[0] + 1;

        long trailerLen = 2L + count + (long)count * 4;
        if (trailerLen > bound.Length) return false;

        long tagsStart = bound.Offset + bound.Length - 2 - count;
        long endsStart = tagsStart - (long)count * 4;
        layout.DataStart = bound.Offset;
        layout.Count = count;
        layout.EndsStart = endsStart;
        layout.TagsStart = tagsStart;
        return true;
    }

    /// <summary>
    /// Exact-match or floor lookup over a ByteTagMap HSST. On success sets
    /// <paramref name="resultBound"/> to the value region of the matched entry.
    /// </summary>
    public static bool TrySeek<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key,
        bool exactMatch, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;
        if (!TryReadLayout<TReader, TPin>(in reader, bound, out Layout L)) return false;

        // Exact-match against this format requires a single-byte key.
        if (exactMatch && key.Length != 1) return false;

        int idx;
        using (TPin tagsPin = reader.PinBuffer(L.TagsStart, L.Count))
        {
            ReadOnlySpan<byte> tags = tagsPin.Buffer;

            if (exactMatch)
            {
                idx = tags.IndexOf(key[0]);
                if (idx < 0) return false;
            }
            else
            {
                // Floor: largest tag whose 1-byte key is ≤ target (lex compare).
                // Tags compare as 1-byte sequences; a multi-byte target with first byte t
                // is strictly greater than the single-byte tag t (shorter is less when
                // the prefix matches), so the floor is still "largest tag ≤ target[0]".
                // An empty target matches nothing.
                if (key.Length == 0) return false;
                byte target = key[0];
                idx = tags.Length - 1;
                while (idx >= 0 && tags[idx] > target) idx--;
                if (idx < 0) return false;
            }
        }

        // Resolve the value bound from Ends. Read Ends[idx] (and Ends[idx-1] when idx > 0)
        // in a single call so the common idx > 0 case is one syscall/read.
        Span<byte> endsBuf = stackalloc byte[8];
        uint prevEnd, thisEnd;
        if (idx == 0)
        {
            if (!reader.TryRead(L.EndsStart, endsBuf[..4])) return false;
            prevEnd = 0;
            thisEnd = BinaryPrimitives.ReadUInt32LittleEndian(endsBuf);
        }
        else
        {
            if (!reader.TryRead(L.EndsStart + (long)(idx - 1) * 4, endsBuf)) return false;
            prevEnd = BinaryPrimitives.ReadUInt32LittleEndian(endsBuf);
            thisEnd = BinaryPrimitives.ReadUInt32LittleEndian(endsBuf[4..]);
        }
        if (thisEnd < prevEnd) return false;

        long valueAbsStart = L.DataStart + prevEnd;
        long valueLen = thisEnd - prevEnd;
        if (valueLen > int.MaxValue) return false;
        resultBound = new Bound(valueAbsStart, (int)valueLen);
        return true;
    }
}
