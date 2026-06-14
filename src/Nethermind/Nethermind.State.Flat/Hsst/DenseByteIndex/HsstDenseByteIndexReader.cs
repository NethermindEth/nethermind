// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Hsst.PackedArray;

namespace Nethermind.State.Flat.Hsst.DenseByteIndex;

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
        if (!HsstPackedArrayLayout.IsValidOffsetSize(offsetSize)) return false;

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

        // Count ≤ 256 (single-byte index) and OffsetSize ≤ 6, so endsTotal ≤ 1.5 KiB.
        long endsTotal = (long)L.Count * L.OffsetSize;
        using TPin endsPin = reader.PinBuffer(new Bound(L.EndsStart, endsTotal));
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

    /// <summary>
    /// Resolve every entry's bound in tag order into <paramref name="dst"/>. Entries with
    /// zero length (gap-filled) get a default <see cref="Bound"/>. Returns the number of
    /// entries written (= <c>Layout.Count</c>), or 0 if the layout is invalid or <paramref name="dst"/>
    /// is too small. Callers size <paramref name="dst"/> to the expected maximum tag + 1
    /// (e.g. 7 for the per-address HSST whose tags are 0x01..0x06). Pins the <c>Ends</c>
    /// array once, avoiding the per-tag re-pin and per-tag layout-read cost of repeated
    /// <see cref="TrySeek{TReader, TPin}"/> calls.
    /// </summary>
    public static int TryResolveAll<TReader, TPin>(
        scoped in TReader reader, Bound bound, Span<Bound> dst)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        if (!TryReadLayout<TReader, TPin>(in reader, bound, out Layout L)) return 0;
        if (L.Count > dst.Length) return 0;
        long endsTotal = (long)L.Count * L.OffsetSize;
        if (endsTotal > int.MaxValue) return 0;
        using TPin endsPin = reader.PinBuffer(new Bound(L.EndsStart, endsTotal));
        ReadOnlySpan<byte> ends = endsPin.Buffer;
        for (int i = 0; i < L.Count; i++)
            TryResolveLocal(L, ends, i, out dst[i]);
        return L.Count;
    }

    private static bool TryResolveLocal(Layout L, ReadOnlySpan<byte> ends, int idx, out Bound entryBound)
    {
        entryBound = default;
        // Producer streams values high-tag → low-tag, so the physical predecessor of tag idx
        // is the next-higher in-array tag (idx + 1). The highest tag (idx == Count − 1) was
        // the first written and starts at DataStart, so its prevEnd is 0.
        long prevEnd = idx == L.Count - 1 ? 0 : ReadEndFixed(ends, (idx + 1) * L.OffsetSize, L.OffsetSize);
        long thisEnd = ReadEndFixed(ends, idx * L.OffsetSize, L.OffsetSize);
        if (thisEnd < prevEnd) return false;
        long valueLen = thisEnd - prevEnd;
        // Bound.Length is long; the only ceiling is the producer's MaxValuesTotal (256 TiB).
        // Stripping the int.MaxValue guard here lets DenseByteIndex columns exceed 2 GiB —
        // hit in practice when the per-address AccountColumn of a long-finality compacted
        // snapshot crosses the 2 GiB mark.
        entryBound = new Bound(L.DataStart + prevEnd, valueLen);
        return true;
    }

    /// <summary>
    /// Read a 1/2/4/6-byte LE end-offset from <paramref name="buf"/> at <paramref name="byteOffset"/>.
    /// Branchless per width: direct integer load for 1/2/4, masked 8-byte unaligned load for 6.
    /// Replaces the prior <c>stackalloc → Clear → CopyTo → ReadUInt64LE</c> shape.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ReadEndFixed(ReadOnlySpan<byte> buf, int byteOffset, int offsetSize) => offsetSize switch
    {
        1 => buf[byteOffset],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(buf[byteOffset..]),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(buf[byteOffset..]),
        // 6-byte LE: load 8 bytes unaligned then mask off the high 16 bits. The 2 bytes past
        // the offset are inside the same Ends[] section (validated by trailerSize) for every
        // entry except the last; the trailer accommodates that with the IndexType + Count +
        // OffsetSize bytes that always follow the array.
        6 => (long)(Unsafe.ReadUnaligned<ulong>(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(buf), (nint)byteOffset))
            & 0x0000_FFFF_FFFF_FFFFul),
        _ => throw new InvalidDataException($"Invalid OffsetSize: {offsetSize}")
    };

    /// <summary>
    /// Resolve the value bound for the single sub-<paramref name="tag"/> within a DenseByteIndex
    /// HSST at <paramref name="bound"/>. Specialised for the per-address inner HSST hot path:
    /// pins one tail window covering <c>IndexType + Count + OffsetSize + Ends[]</c> in a single
    /// <see cref="IHsstByteReader{TPin}.PinBuffer"/> call instead of the three reader calls the
    /// general dispatch path uses (one byte for <see cref="IndexType"/>, two for the layout
    /// header, one pin for <c>Ends[]</c>).
    /// </summary>
    /// <remarks>
    /// Validation mirrors <see cref="TryReadLayout{TReader, TPin}"/>: rejects an
    /// <see cref="IndexType"/> mismatch, an invalid <c>OffsetSize</c>, a truncated bound, and
    /// returns <c>false</c> for <paramref name="tag"/> ≥ Count (matches the exact-match semantics
    /// of <see cref="TrySeek{TReader, TPin}"/>). Empty entries (gap-fill) return <c>true</c> with
    /// a zero-length <see cref="Bound"/> — callers check <c>Length == 0</c> for absence.
    ///
    /// The pinned window is sized to fit the per-address HSST's trailer in one shot (Count ≤ 7,
    /// OffsetSize ∈ {1, 2}, trailer ≤ 17 bytes); larger trailers fall back to a precise re-pin
    /// of the <c>Ends[]</c> array.
    /// </remarks>
    public static bool TryResolveSingleTag<TReader, TPin>(
        scoped in TReader reader, Bound bound, byte tag, out Bound entryBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        entryBound = default;
        if (bound.Length < 3) return false;

        int winLen = (int)Math.Min(SpecTailWindow, bound.Length);
        long winStart = bound.Offset + bound.Length - winLen;
        using TPin winPin = reader.PinBuffer(new Bound(winStart, winLen));
        ReadOnlySpan<byte> win = winPin.Buffer;

        // Trailer layout (low → high address): [Ends[count]] [Count u8] [OffsetSize u8] [IndexType u8].
        if (win[winLen - 1] != (byte)IndexType.DenseByteIndex) return false;
        int count = win[winLen - 3] + 1;
        int offsetSize = win[winLen - 2];
        if (!HsstPackedArrayLayout.IsValidOffsetSize(offsetSize)) return false;

        long endsBytes = (long)count * offsetSize;
        long trailerSize = 3L + endsBytes;
        if (trailerSize > bound.Length) return false;
        if ((uint)tag >= (uint)count) return false;

        if (trailerSize <= winLen)
        {
            int endsOffsetInWin = winLen - 3 - (int)endsBytes;
            return ResolveTag(win.Slice(endsOffsetInWin, (int)endsBytes), count, offsetSize, tag,
                              bound.Offset, out entryBound);
        }

        // Cold path: trailer exceeds the speculative window (count > ~13 with offsetSize 2, or
        // any combination beyond SpecTailWindow). Re-pin Ends[] precisely.
        if (endsBytes > int.MaxValue) return false;
        using TPin endsPin = reader.PinBuffer(new Bound(bound.Offset + bound.Length - trailerSize, endsBytes));
        return ResolveTag(endsPin.Buffer, count, offsetSize, tag, bound.Offset, out entryBound);
    }

    /// <summary>Speculative tail window for <see cref="TryResolveSingleTag"/>. Sized to cover the
    /// per-address inner HSST's trailer (Count ≤ 7, OffsetSize ∈ {1, 2} ⇒ ≤ 17 bytes) with room
    /// for format growth. Larger trailers fall back to a precise re-pin.</summary>
    private const int SpecTailWindow = 32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ResolveTag(ReadOnlySpan<byte> ends, int count, int offsetSize, int tag,
                                   long dataStart, out Bound entryBound)
    {
        long prevEnd = tag == count - 1 ? 0L : ReadEndFixed(ends, (tag + 1) * offsetSize, offsetSize);
        long thisEnd = ReadEndFixed(ends, tag * offsetSize, offsetSize);
        if (thisEnd < prevEnd) { entryBound = default; return false; }
        entryBound = new Bound(dataStart + prevEnd, thisEnd - prevEnd);
        return true;
    }
}
