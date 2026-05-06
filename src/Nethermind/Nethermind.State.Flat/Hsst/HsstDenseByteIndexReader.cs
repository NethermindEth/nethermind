// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.DenseByteIndex"/> layout. Stateless
/// static methods so <see cref="HsstReader{TReader,TPin}"/> can dispatch into them
/// without copying its ref-struct state.
/// </summary>
internal static class HsstDenseByteIndexReader
{
    /// <summary>Parsed footer of a DenseByteIndex HSST.</summary>
    internal struct Layout
    {
        /// <summary>Absolute offset of byte 0 of the HSST (= start of the value region).</summary>
        public long DataStart;
        /// <summary>Number of entries (= N; valid tag indices are 0..N − 1).</summary>
        public int Count;
        /// <summary>Absolute offset of the <c>Ends</c> array (4·Count bytes).</summary>
        public long EndsStart;
    }

    /// <summary>
    /// Parse the DenseByteIndex trailer. Returns false on truncation. Caller must
    /// have already verified the trailing <see cref="IndexType"/> byte equals
    /// <see cref="IndexType.DenseByteIndex"/>.
    /// </summary>
    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        if (bound.Length < 2) return false;

        Span<byte> oneByte = stackalloc byte[1];
        if (!reader.TryRead(bound.Offset + bound.Length - 2, oneByte)) return false;
        // Count byte stores N − 1; the empty map cannot be represented.
        int count = oneByte[0] + 1;

        long trailerLen = 2L + (long)count * 4;
        if (trailerLen > bound.Length) return false;

        long endsStart = bound.Offset + bound.Length - 2 - (long)count * 4;
        layout.DataStart = bound.Offset;
        layout.Count = count;
        layout.EndsStart = endsStart;
        return true;
    }

    /// <summary>
    /// Exact-match or floor lookup over a DenseByteIndex HSST. The <paramref name="key"/>
    /// must be a single byte (multi-byte/empty rejects). Floor semantics: largest tag
    /// index <c>≤ key[0]</c> whose entry length is non-zero (gap entries are skipped).
    /// </summary>
    public static bool TrySeek<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key,
        bool exactMatch, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;
        if (!TryReadLayout<TReader, TPin>(in reader, bound, out Layout L)) return false;

        // Single-byte keys only (matches the producer-side contract).
        if (key.Length != 1) return false;
        int target = key[0];

        if (exactMatch)
        {
            if ((uint)target >= (uint)L.Count) return false;
            return ResolveEntryBound<TReader, TPin>(in reader, L, target, out resultBound);
        }

        // Floor: walk back from min(target, Count − 1) and skip zero-length entries.
        int idx = target < L.Count ? target : L.Count - 1;
        while (idx >= 0)
        {
            if (!ResolveEntryBound<TReader, TPin>(in reader, L, idx, out Bound b))
                return false;
            if (b.Length > 0)
            {
                resultBound = b;
                return true;
            }
            idx--;
        }
        return false;
    }

    private static bool ResolveEntryBound<TReader, TPin>(scoped in TReader reader, Layout L, int idx, out Bound entryBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        entryBound = default;
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
        long valueLen = thisEnd - prevEnd;
        if (valueLen > int.MaxValue) return false;
        entryBound = new Bound(L.DataStart + prevEnd, (int)valueLen);
        return true;
    }
}
