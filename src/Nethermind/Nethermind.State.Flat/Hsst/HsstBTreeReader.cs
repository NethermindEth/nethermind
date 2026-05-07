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

        // Root node ends just before the IndexType byte.
        long currentAbsEnd = bound.Offset + bound.Length - 1;

        while (true)
        {
            if (!TryLoadNode<TReader, TPin>(in reader, currentAbsEnd, out HsstIndex node, out _, out TPin pin))
                return false;
            using (pin)
            {
                if (node.IsIntermediate)
                {
                    if (!node.TryGetFloor(key, out _, out ReadOnlySpan<byte> childValueBytes))
                        return false;
                    ulong childOffset = BSearchIndex.BSearchIndexReader.ReadUInt64LE(childValueBytes) + node.Metadata.BaseOffset;
                    // childOffset is the inclusive last byte of the child node (0-indexed within the HSST).
                    // Exclusive end in reader-absolute terms = bound.Offset + childOffset + 1.
                    currentAbsEnd = bound.Offset + (long)childOffset + 1;
                    continue;
                }

                if (!node.TryGetFloor(key, out ReadOnlySpan<byte> separator, out ReadOnlySpan<byte> metaBytes))
                    return false;

                // Cheap reject path: the stored full key starts with (commonPrefix + separator),
                // so the input must too. Saves a length-mismatch read in the common
                // exact-miss case.
                if (exactMatch)
                {
                    ReadOnlySpan<byte> p = node.CommonKeyPrefix;
                    if (!key.StartsWith(p) || !key[p.Length..].StartsWith(separator)) return false;
                }

                ulong metaStart = BSearchIndex.BSearchIndexReader.ReadUInt64LE(metaBytes) + node.Metadata.BaseOffset;
                long absMetaStart = bound.Offset + (long)metaStart;

                // Read up to 11 bytes from absMetaStart: enough for ValueLength (≤10
                // for long LEB128) + KeyLength (1 byte). KeyLength only consumed when
                // exact-matching.
                long available = bound.Offset + bound.Length - absMetaStart;
                if (available <= 0) return false;
                Span<byte> lebBuf = stackalloc byte[11];
                int lebRead = (int)Math.Min(11, available);
                if (!reader.TryRead(absMetaStart, lebBuf[..lebRead])) return false;

                int pos = 0;
                long valueLength = Leb128.Read(lebBuf, ref pos);

                if (exactMatch)
                {
                    if (pos >= lebRead) return false;
                    int keyLength = lebBuf[pos++];
                    if (keyLength != key.Length) return false;

                    // Stored key fits in 255 bytes — single read + compare, no chunking.
                    Span<byte> stored = stackalloc byte[255];
                    Span<byte> storedSlice = stored[..keyLength];
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
    /// Load the index node whose exclusive end is <paramref name="absEnd"/> via the reader's
    /// <see cref="IHsstByteReader{TPin}.PinBuffer"/>. On success outs the parsed <see cref="HsstIndex"/>,
    /// the node's absolute start offset, and the pin (whose <see cref="IBufferPin.Buffer"/> backs
    /// <paramref name="node"/>). The caller must dispose the pin once it's done with the node.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryLoadNode<TReader, TPin>(
        scoped in TReader reader, long absEnd,
        out HsstIndex node, out long nodeAbsStart, out TPin pin)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        node = default;
        nodeAbsStart = 0;
        pin = default;

        if (absEnd < 12) return false;

        // BSearchIndex footer is fixed-width; its tail is 6 bytes
        //   [valueSize u8][keySize u16][keyCount u16][flags u8]
        // preceded by a mandatory 6-byte BaseOffset and an optional
        // [common-prefix bytes][prefixLen u8]. Common-prefix is capped at 128
        // bytes by the layout planner; pin a bounded window covering the
        // worst-case footer so the entire block is in one read.
        const int MaxFooterBytes = 6 + 1 + 128 + 6;
        long footerStart = Math.Max(0, absEnd - MaxFooterBytes);
        int footerLen = (int)(absEnd - footerStart);

        int totalNodeSize;
        using (TPin metaPin = reader.PinBuffer(footerStart, footerLen))
        {
            ReadOnlySpan<byte> metaSpan = metaPin.Buffer;
            byte flags = metaSpan[footerLen - 1];
            int valueSize = metaSpan[footerLen - 6];
            int keySize = BinaryPrimitives.ReadUInt16LittleEndian(metaSpan[(footerLen - 5)..]);
            int keyCount = BinaryPrimitives.ReadUInt16LittleEndian(metaSpan[(footerLen - 3)..]);
            int keyType = (flags >> 1) & 0x03;
            int valueType = (flags >> 3) & 0x03;
            int keySectionSize = keyType switch { 0 => keySize, _ => keyCount * keySize };
            int valueSectionSize = valueType switch { 0 => valueSize, _ => keyCount * valueSize };
            int extraFooter = 6; // mandatory BaseOffset
            if ((flags & 0x40) != 0)
            {
                int prefixLen = metaSpan[footerLen - 7];
                extraFooter += 1 + prefixLen;
            }
            totalNodeSize = valueSectionSize + keySectionSize + 6 + extraFooter;
        }

        nodeAbsStart = absEnd - totalNodeSize;
        if (nodeAbsStart < 0) return false;

        pin = reader.PinBuffer(nodeAbsStart, totalNodeSize);
        node = HsstIndex.ReadFromEnd(pin.Buffer, totalNodeSize);
        return true;
    }
}
