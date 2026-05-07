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
    // Crossover where binary search beats vectorized IndexOf / backward floor scan on
    // sorted single-byte tag arrays. The ≤7 and ≤3 ByteTagMap call sites stay on the
    // linear path; the ≤256 slot-suffix bucket takes the binary-search path.
    private const int BinarySearchThreshold = 16;

    /// <summary>On-disk end-offset width: fixed 2 bytes (u16 LE), matching the builder.</summary>
    private const int OffsetSize = 2;

    /// <summary>Parsed footer of a ByteTagMap HSST.</summary>
    internal struct Layout
    {
        /// <summary>Absolute offset of byte 0 of the HSST (= start of the value region).</summary>
        public long DataStart;
        /// <summary>Number of entries.</summary>
        public int Count;
        /// <summary>Absolute offset of the <c>Ends</c> array (<c>Count·2</c> bytes, u16 LE).</summary>
        public long EndsStart;
        /// <summary>Absolute offset of the <c>Tags</c> array (Count bytes, adjacent to the trailer).</summary>
        public long TagsStart;
    }

    /// <summary>
    /// Parse the ByteTagMap trailer. Returns false on truncation. Caller must have
    /// already verified the trailing <see cref="IndexType"/> byte equals
    /// <see cref="IndexType.ByteTagMap"/>.
    /// </summary>
    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        if (bound.Length < 2) return false;

        // Read Count from position -2 (IndexType at -1 was already verified).
        Span<byte> hdr = stackalloc byte[1];
        if (!reader.TryRead(bound.Offset + bound.Length - 2, hdr)) return false;
        // Count byte stores N - 1; the empty map cannot be represented by this format.
        int count = hdr[0] + 1;

        long trailerLen = 2L + count + (long)count * OffsetSize;
        if (trailerLen > bound.Length) return false;

        long tagsStart = bound.Offset + bound.Length - 2 - count;
        long endsStart = tagsStart - (long)count * OffsetSize;
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
                if (tags.Length >= BinarySearchThreshold)
                {
                    byte needle = key[0];
                    int lo = 0, hi = tags.Length - 1;
                    idx = -1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >>> 1;
                        byte t = tags[mid];
                        if (t == needle) { idx = mid; break; }
                        if (t < needle) lo = mid + 1; else hi = mid - 1;
                    }
                    if (idx < 0) return false;
                }
                else
                {
                    idx = tags.IndexOf(key[0]);
                    if (idx < 0) return false;
                }
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
                if (tags.Length >= BinarySearchThreshold)
                {
                    // Upper bound: first index i with tags[i] > target; floor is i - 1.
                    int lo = 0, hi = tags.Length;
                    while (lo < hi)
                    {
                        int mid = (lo + hi) >>> 1;
                        if (tags[mid] <= target) lo = mid + 1; else hi = mid;
                    }
                    idx = lo - 1;
                    if (idx < 0) return false;
                }
                else
                {
                    idx = tags.Length - 1;
                    while (idx >= 0 && tags[idx] > target) idx--;
                    if (idx < 0) return false;
                }
            }
        }

        // Resolve the value bound from Ends. Read both Ends[idx-1] and Ends[idx] in one
        // call when idx > 0 so the common path is a single syscall/read.
        Span<byte> endsBuf = stackalloc byte[2 * OffsetSize];
        int prevEnd, thisEnd;
        if (idx == 0)
        {
            if (!reader.TryRead(L.EndsStart, endsBuf[..OffsetSize])) return false;
            prevEnd = 0;
            thisEnd = BinaryPrimitives.ReadUInt16LittleEndian(endsBuf);
        }
        else
        {
            if (!reader.TryRead(L.EndsStart + (long)(idx - 1) * OffsetSize, endsBuf)) return false;
            prevEnd = BinaryPrimitives.ReadUInt16LittleEndian(endsBuf);
            thisEnd = BinaryPrimitives.ReadUInt16LittleEndian(endsBuf[OffsetSize..]);
        }
        if (thisEnd < prevEnd) return false;

        resultBound = new Bound(L.DataStart + prevEnd, thisEnd - prevEnd);
        return true;
    }
}
