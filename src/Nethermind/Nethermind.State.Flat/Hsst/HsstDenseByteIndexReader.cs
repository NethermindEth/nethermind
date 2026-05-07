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
        /// <summary>Per-end-offset width on disk: 1, 2, 4, or 6 bytes.</summary>
        public int OffsetSize;
        /// <summary>Absolute offset of the <c>Ends</c> array (<c>Count·OffsetSize</c> bytes).</summary>
        public long EndsStart;
    }

    /// <summary>
    /// Parse the DenseByteIndex trailer. Returns false on truncation or invalid OffsetSize.
    /// Caller must have already verified the trailing <see cref="IndexType"/> byte equals
    /// <see cref="IndexType.DenseByteIndex"/>.
    /// </summary>
    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        if (bound.Length < 3) return false;

        // Read [Count, OffsetSize] at positions [-3..-1) (IndexType at -1 was already verified).
        Span<byte> hdr = stackalloc byte[2];
        if (!reader.TryRead(bound.Offset + bound.Length - 3, hdr)) return false;
        // Count byte stores N − 1; the empty map cannot be represented.
        int count = hdr[0] + 1;
        int offsetSize = hdr[1];
        if (!HsstOffset.IsValidOffsetSize(offsetSize)) return false;

        long trailerLen = 3L + (long)count * offsetSize;
        if (trailerLen > bound.Length) return false;

        long endsStart = bound.Offset + bound.Length - 3 - (long)count * offsetSize;
        layout.DataStart = bound.Offset;
        layout.Count = count;
        layout.OffsetSize = offsetSize;
        layout.EndsStart = endsStart;
        return true;
    }

    /// <summary>
    /// Exact-match or floor lookup over a DenseByteIndex HSST. The <paramref name="key"/>
    /// must be a single byte (multi-byte/empty rejects). Floor semantics: largest tag
    /// index <c>≤ key[0]</c> whose entry length is non-zero (gap entries are skipped).
    ///
    /// Pins the entire <c>Ends</c> array once (≤ Count·OffsetSize bytes ≤ 1.5 KiB) and
    /// resolves entry bounds locally. Avoids the previous per-entry <c>TryRead</c> for
    /// gap-skipping floor walks, where sparse maps could pay one read per zero-length
    /// entry.
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

        long endsTotal = (long)L.Count * L.OffsetSize;
        if (endsTotal > int.MaxValue) return false;
        using TPin endsPin = reader.PinBuffer(L.EndsStart, endsTotal);
        ReadOnlySpan<byte> ends = endsPin.Buffer;

        if (exactMatch)
        {
            if ((uint)target >= (uint)L.Count) return false;
            return TryResolveLocal(L, ends, target, out resultBound);
        }

        // Floor: walk back from min(target, Count − 1) and skip zero-length entries.
        // Reads are now span slices — no IO per gap.
        int idx = target < L.Count ? target : L.Count - 1;
        while (idx >= 0)
        {
            if (!TryResolveLocal(L, ends, idx, out Bound b)) return false;
            if (b.Length > 0)
            {
                resultBound = b;
                return true;
            }
            idx--;
        }
        return false;
    }

    private static bool TryResolveLocal(Layout L, ReadOnlySpan<byte> ends, int idx, out Bound entryBound)
    {
        entryBound = default;
        long prevEnd = idx == 0 ? 0 : ReadEnd(ends, (idx - 1) * L.OffsetSize, L.OffsetSize);
        long thisEnd = ReadEnd(ends, idx * L.OffsetSize, L.OffsetSize);
        if (thisEnd < prevEnd) return false;
        long valueLen = thisEnd - prevEnd;
        if (valueLen > int.MaxValue) return false;
        entryBound = new Bound(L.DataStart + prevEnd, (int)valueLen);
        return true;
    }

    /// <summary>Read a 1/2/4/6-byte LE end-offset from <paramref name="buf"/> at <paramref name="byteOffset"/>.</summary>
    private static long ReadEnd(ReadOnlySpan<byte> buf, int byteOffset, int offsetSize)
    {
        Span<byte> wide = stackalloc byte[8];
        wide.Clear();
        buf.Slice(byteOffset, offsetSize).CopyTo(wide);
        return (long)BinaryPrimitives.ReadUInt64LittleEndian(wide);
    }
}
