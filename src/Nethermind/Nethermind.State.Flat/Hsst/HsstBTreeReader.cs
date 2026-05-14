// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.BTree"/> layout. Stateless static
/// methods so <see cref="HsstReader{TReader,TPin}"/> can dispatch into them without
/// copying its ref-struct state.
/// </summary>
internal static class HsstBTreeReader
{
    /// <summary>
    /// Exact-match or floor lookup over a BTree HSST. On success sets
    /// <paramref name="resultBound"/> to the value region of the matched entry. Caller
    /// has already read the trailing <see cref="IndexType"/> byte.
    /// </summary>
    public static bool TrySeek<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key,
        bool exactMatch, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;

        // Trailer is [RootSize u16 LE][KeyLength u8][IndexType u8]. Root start = bound end - 4 - RootSize.
        if (bound.Length < 4 + 12) return false;
        Span<byte> trailerBuf = stackalloc byte[3];
        if (!reader.TryRead(bound.Offset + bound.Length - 4, trailerBuf)) return false;
        int rootSize = trailerBuf[0] | (trailerBuf[1] << 8);
        int trailerKeyLength = trailerBuf[2];

        // Exact-match needs the input key to match the HSST's fixed key length; reject up
        // front before walking the tree. Floor lookups intentionally allow mismatched
        // lengths so callers can seek with a key prefix or sentinel.
        if (exactMatch && key.Length != trailerKeyLength) return false;

        long currentAbsStart = bound.Offset + bound.Length - 4 - rootSize;
        // Trailer is 4 bytes; nodes live in [bound.Offset, scopeEnd).
        long scopeEnd = bound.Offset + bound.Length - 4;

        while (true)
        {
            if (!TryLoadNode<TReader, TPin>(in reader, currentAbsStart, scopeEnd, out HsstIndex node, out TPin pin))
                return false;
            using (pin)
            {
                if (node.IsIntermediate)
                {
                    // Intermediate nodes drop the phantom leftmost slot: keys array
                    // holds the N-1 real separators between adjacent children, and
                    // BaseOffset names the leftmost child directly. A "no floor"
                    // search result (key < smallest separator, or empty 1-child
                    // node) routes to the leftmost child via BaseOffset alone.
                    long childOffset = node.TryGetFloor(key, out _, out ReadOnlySpan<byte> childValueBytes)
                        ? (long)(BSearchIndex.BSearchIndexReader.ReadUInt64LE(childValueBytes) + node.Metadata.BaseOffset)
                        : (long)node.Metadata.BaseOffset;
                    // childOffset is the first byte of the child node (0-indexed within the HSST).
                    currentAbsStart = bound.Offset + childOffset;
                    continue;
                }

                if (!node.TryGetFloor(key, out ReadOnlySpan<byte> separator, out ReadOnlySpan<byte> metaBytes))
                    return false;

                // Cheap reject path: the stored full key starts with (commonPrefix + separator),
                // so the input must too. Saves a length-mismatch read in the common
                // exact-miss case. Skip when the leaf stores keys in LE byte order — the
                // `separator` bytes are byte-reversed, so a direct StartsWith comparison would
                // be incorrect, and the storage-read SequenceEqual below still catches mismatches.
                if (exactMatch && !node.Metadata.IsKeyLittleEndian)
                {
                    ReadOnlySpan<byte> p = node.CommonKeyPrefix;
                    if (!key.StartsWith(p) || !key[p.Length..].StartsWith(separator)) return false;
                }

                long metaStart = (long)(BSearchIndex.BSearchIndexReader.ReadUInt64LE(metaBytes) + node.Metadata.BaseOffset);
                long absMetaStart = bound.Offset + metaStart;

                // Read up to 10 bytes from absMetaStart for the ValueLength LEB128 (max
                // 10 bytes for a 64-bit varint). The key length comes from the trailer,
                // not from per-entry storage.
                long available = bound.Offset + bound.Length - absMetaStart;
                if (available <= 0) return false;
                Span<byte> lebBuf = stackalloc byte[10];
                int lebRead = (int)Math.Min(10, available);
                if (!reader.TryRead(absMetaStart, lebBuf[..lebRead])) return false;

                int pos = 0;
                long valueLength = Leb128.Read(lebBuf, ref pos);

                if (exactMatch)
                {
                    // trailerKeyLength == key.Length was already enforced at the top of
                    // TrySeek; compare the stored key bytes against the input. Stored key
                    // fits in 255 bytes — single read + compare, no chunking.
                    Span<byte> stored = stackalloc byte[255];
                    Span<byte> storedSlice = stored[..trailerKeyLength];
                    if (!reader.TryRead(absMetaStart + pos, storedSlice)) return false;
                    if (!storedSlice.SequenceEqual(key)) return false;
                }

                // value bytes are immediately before the metaStart
                resultBound = new Bound(absMetaStart - valueLength, valueLength);
                return true;
            }
        }
    }

    /// <summary>
    /// Speculative pin window. Sized to cover a typical small leaf body in one read; nodes
    /// aren't page-aligned so there's no gain from rounding up further. Larger leaves and
    /// intermediates fall back to a precise re-pin.
    /// </summary>
    private const int SpeculativePinSize = 1024;

    /// <summary>
    /// Load the index node whose first byte is at <paramref name="absStart"/> via the reader's
    /// <see cref="IHsstByteReader{TPin}.PinBuffer"/>. On success outs the parsed <see cref="HsstIndex"/>
    /// and the pin (whose <see cref="IBufferPin.Buffer"/> backs <paramref name="node"/>). The
    /// caller must dispose the pin once it's done with the node.
    ///
    /// Issues a single speculative pin sized to <see cref="SpeculativePinSize"/> in the common
    /// case: the header at the front of the window is parsed to compute totalNodeSize, and when
    /// the node fits inside the speculative window we keep that pin instead of re-pinning
    /// precisely. The forward layout means the prefetcher pulls keys/values during the header
    /// read. Cold path (oversized leaves) disposes the speculative pin and re-pins exactly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryLoadNode<TReader, TPin>(
        scoped in TReader reader, long absStart, long scopeEnd,
        out HsstIndex node, out TPin pin)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        node = default;
        pin = default;

        long available = scopeEnd - absStart;
        if (available < 12) return false;

        int winLen = (int)Math.Min(SpeculativePinSize, available);

        TPin speculativePin = reader.PinBuffer(absStart, winLen);
        bool keepSpeculative = false;
        int totalNodeSize;
        try
        {
            ReadOnlySpan<byte> win = speculativePin.Buffer;
            byte flags = win[0];
            int keyCount = BinaryPrimitives.ReadUInt16LittleEndian(win[1..]);
            int keySize = BinaryPrimitives.ReadUInt16LittleEndian(win[3..]);
            int valueSize = win[5];
            // BaseOffset (6 bytes) at win[6..12]; we don't need it here, just the size.
            int headerSize = 12;
            if ((flags & 0x40) != 0)
            {
                if (winLen < 13) goto Cold;
                int prefixLen = win[12];
                headerSize += 1 + prefixLen;
            }
            int keyType = (flags >> 1) & 0x03;
            int valueType = (flags >> 3) & 0x03;
            int keySectionSize = keyType switch { 0 => keySize, _ => keyCount * keySize };
            int valueSectionSize = valueType switch { 0 => valueSize, _ => keyCount * valueSize };
            totalNodeSize = headerSize + keySectionSize + valueSectionSize;

            if (totalNodeSize <= winLen)
            {
                // Hot path: node fits in the speculative window. ReadFromStart parses the
                // header at win[0..] and slices keys/values forward within the node range.
                node = HsstIndex.ReadFromStart(win, 0);
                pin = speculativePin;
                keepSpeculative = true;
                return true;
            }
        }
        finally
        {
            if (!keepSpeculative) speculativePin.Dispose();
        }

        // Cold path: node larger than the speculative window. Pin precisely.
        pin = reader.PinBuffer(absStart, totalNodeSize);
        node = HsstIndex.ReadFromStart(pin.Buffer, 0);
        return true;

    Cold:
        // Window too small to even read the common-prefix length byte. The HasCommonKeyPrefix
        // bit is set yet available < 13, which is structurally impossible for a well-formed
        // HSST — bail rather than risk an out-of-bounds read.
        return false;
    }
}
