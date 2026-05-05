// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.BTree"/> and
/// <see cref="IndexType.BTreeHashIndex"/> layouts. Stateless static methods so
/// <see cref="HsstReader{TReader,TPin}"/> can dispatch into them without copying its
/// ref-struct state.
/// </summary>
internal static class HsstBTreeReader
{
    /// <summary>
    /// Exact-match or floor lookup over a BTree (optionally with appended hash index) HSST.
    /// On success sets <paramref name="resultBound"/> to the value region of the matched entry.
    /// Caller has already read the trailing <see cref="IndexType"/> byte and decoded which of
    /// the two layouts this is via <paramref name="hasHashIndex"/>.
    /// </summary>
    public static bool TrySeek<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key,
        bool exactMatch, bool hasHashIndex, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;

        // Root node ends just before the IndexType byte (or before the hash index region).
        long currentAbsEnd = bound.Offset + bound.Length - 1;

        if (hasHashIndex)
        {
            // Hash table layout (read backward from IndexType byte):
            //   [HashTable: N * 4 bytes][TableSize: u32 LE][IndexType: u8]
            Span<byte> sizeBuf = stackalloc byte[4];
            if (!reader.TryRead(bound.Offset + bound.Length - 5, sizeBuf)) return false;
            uint tableSizeU = BinaryPrimitives.ReadUInt32LittleEndian(sizeBuf);
            if (tableSizeU == 0 || tableSizeU > int.MaxValue) return false;
            int tableSize = (int)tableSizeU;
            long tableBytes = (long)tableSize * 4;
            long tableStart = bound.Offset + bound.Length - 5 - tableBytes;
            if (tableStart < bound.Offset) return false;

            // Root b-tree node ends right before the hash table.
            currentAbsEnd = tableStart;

            // Probe the slot. We always need an exact key compare even for floor,
            // because the slot only narrows down to a single candidate; if the key
            // doesn't match, we fall through to the b-tree.
            uint h = HsstHash.HashKey(key);
            uint slot = HsstHash.Slot(h, tableSize);
            Span<byte> slotBuf = stackalloc byte[4];
            if (!reader.TryRead(tableStart + slot * 4, slotBuf)) return false;
            uint slotValue = BinaryPrimitives.ReadUInt32LittleEndian(slotBuf);

            const uint Empty = 0u;
            const uint Collision = 0xFFFFFFFFu;

            if (slotValue == Empty)
            {
                // Definitively no entry hashes here. Exact match cannot succeed.
                // Floor still needs the b-tree (to find the largest key < input).
                if (exactMatch) return false;
                // Fall through to b-tree walk for floor.
            }
            else if (slotValue == Collision)
            {
                // Multiple entries collided at this slot. Fall through to b-tree.
            }
            else
            {
                int metaStart = (int)slotValue;
                long absMetaStart = bound.Offset + metaStart;

                long available = bound.Offset + bound.Length - absMetaStart;
                if (available <= 0) return false;
                Span<byte> lebBuf = stackalloc byte[6];
                int lebRead = (int)Math.Min(6, available);
                if (!reader.TryRead(absMetaStart, lebBuf[..lebRead])) return false;
                int pos = 0;
                int valueLength = Leb128.Read(lebBuf, ref pos);

                // The hash slot only resolves to one candidate entry; we must verify
                // the key matches before accepting (false-positive collisions are
                // impossible given the empty-slot semantics, but a different key with
                // the same hash slot is rejected here too).
                if (pos >= lebRead) return false;
                int keyLength = lebBuf[pos++];
                if (keyLength != key.Length)
                {
                    if (exactMatch) return false;
                    // Floor: fall through to b-tree.
                }
                else
                {
                    Span<byte> stored = stackalloc byte[255];
                    Span<byte> storedSlice = stored[..keyLength];
                    if (!reader.TryRead(absMetaStart + pos, storedSlice)) return false;
                    if (!storedSlice.SequenceEqual(key))
                    {
                        if (exactMatch) return false;
                        // Floor: fall through to b-tree.
                    }
                    else
                    {
                        resultBound = new Bound(absMetaStart - valueLength, valueLength);
                        return true;
                    }
                }
            }
        }

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
                    int childOffset = BinaryPrimitives.ReadInt32LittleEndian(childValueBytes) + node.Metadata.BaseOffset;
                    // childOffset is the inclusive last byte of the child node (0-indexed within the HSST).
                    // Exclusive end in reader-absolute terms = bound.Offset + childOffset + 1.
                    currentAbsEnd = bound.Offset + childOffset + 1;
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

                int metaStart = BinaryPrimitives.ReadInt32LittleEndian(metaBytes) + node.Metadata.BaseOffset;
                long absMetaStart = bound.Offset + metaStart;

                // Read up to 6 bytes from absMetaStart: enough for ValueLength (≤5)
                // LEB128 + KeyLength (1 byte). KeyLength only consumed when exact-matching.
                long available = bound.Offset + bound.Length - absMetaStart;
                if (available <= 0) return false;
                Span<byte> lebBuf = stackalloc byte[6];
                int lebRead = (int)Math.Min(6, available);
                if (!reader.TryRead(absMetaStart, lebBuf[..lebRead])) return false;

                int pos = 0;
                int valueLength = Leb128.Read(lebBuf, ref pos);

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
    private static bool TryLoadNode<TReader, TPin>(
        scoped in TReader reader, long absEnd,
        out HsstIndex node, out long nodeAbsStart, out TPin pin)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        node = default;
        nodeAbsStart = 0;
        pin = default;

        if (absEnd < 1) return false;

        // Read the trailing MetadataLength byte
        Span<byte> oneByte = stackalloc byte[1];
        if (!reader.TryRead(absEnd - 1, oneByte)) return false;
        int metadataLen = oneByte[0];

        long metadataAbsStart = absEnd - 1 - metadataLen;
        if (metadataAbsStart < 0) return false;

        int totalNodeSize;
        using (TPin metaPin = reader.PinBuffer(metadataAbsStart, metadataLen))
        {
            ReadOnlySpan<byte> metaSpan = metaPin.Buffer;
            int p = 0;
            byte flags = metaSpan[p++];
            byte extFlags = 0;
            if ((flags & 0x80) != 0) extFlags = metaSpan[p++];
            int keyCount = Leb128.Read(metaSpan, ref p);
            int keySize = Leb128.Read(metaSpan, ref p);
            int valueSize = Leb128.Read(metaSpan, ref p);
            // BaseOffset is consumed by HsstIndex.ReadFromEnd; we only need section sizes here.
            int keyType = (flags >> 1) & 0x03;
            int valueType = (flags >> 3) & 0x03;
            int keySectionSize = keyType switch { 0 => keySize, _ => keyCount * keySize };
            int valueSectionSize = valueType switch { 0 => valueSize, _ => keyCount * valueSize };
            int probeSize = 0;
            if (keyCount > 0)
            {
                if ((extFlags & 0x01) != 0) probeSize = HsstHash.BucketCount(keyCount);
                else if ((extFlags & 0x02) != 0) probeSize = HsstHash.BucketCount(keyCount) * 2;
            }
            totalNodeSize = valueSectionSize + keySectionSize + probeSize + metadataLen + 1;
        }

        nodeAbsStart = absEnd - totalNodeSize;
        if (nodeAbsStart < 0) return false;

        pin = reader.PinBuffer(nodeAbsStart, totalNodeSize);
        node = HsstIndex.ReadFromEnd(pin.Buffer, totalNodeSize);
        return true;
    }
}
