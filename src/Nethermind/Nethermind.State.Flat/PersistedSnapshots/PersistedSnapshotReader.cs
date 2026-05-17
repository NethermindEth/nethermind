// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Hsst;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Static decoding/reading helpers for persisted-snapshot HSST data. All "read by key"
/// helpers consume an <see cref="IHsstByteReader{TPin}"/> and emit <see cref="Bound"/>s;
/// callers materialise spans from the reader as needed. Streaming column scans live in
/// <see cref="PersistedSnapshotScanner"/>.
/// </summary>
public static class PersistedSnapshotReader
{
    private const int TopPathThreshold = 7;
    private const int CompactPathThreshold = 15;
    private const int SlotPrefixLength = 30;

    /// <summary>
    /// Seek the per-address inner-HSST bound under <see cref="PersistedSnapshotTags.AccountColumnTag"/>:
    /// AccountColumnTag → addressHash.Bytes[..PersistedSnapshotTags.AddressHashPrefixLength]. On success outs the
    /// inner-HSST bound that <see cref="HsstReader{TReader,TPin}"/> can be re-entered with to
    /// do sub-tag lookups (storage-trie nodes, slots, account, self-destruct, raw-address
    /// preimage) without re-walking the outer column.
    /// </summary>
    internal static bool TryGetAddressHsstBound<TReader, TPin>(scoped in TReader reader, in ValueHash256 addressHash, out Bound addressBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshotTags.AccountColumnTag, out _) ||
            !r.TrySeek(addressHash.Bytes[..PersistedSnapshotTags.AddressHashPrefixLength], out _))
        {
            addressBound = default;
            return false;
        }
        addressBound = r.GetBound();
        return true;
    }

    internal static bool TryGetAccount<TReader, TPin>(scoped in TReader reader, Bound addressBound, out Bound accountBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        // Per-address HSST is always DenseByteIndex (column 0x01 layout). Resolve the sub-tag
        // in a single pinned trailer read instead of going through HsstReader's dispatch +
        // separate IndexType / layout / Ends[] reads. DenseByteIndex returns success for any
        // tag below count, including gap-filled (length 0) absences; treat length 0 as "no
        // account record" so callers don't misread an absent entry as a deleted account.
        if (!HsstDenseByteIndexReader.TryResolveSingleTag<TReader, TPin>(
                in reader, addressBound, PersistedSnapshotTags.AccountSubTagByte, out Bound b) ||
            b.Length == 0)
        {
            accountBound = default;
            return false;
        }
        accountBound = b;
        return true;
    }

    internal static bool TryGetSlot<TReader, TPin>(scoped in TReader reader, Bound addressBound, in UInt256 index, out Bound slotBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        // Per-address sub-tag step is always DenseByteIndex — resolve in one pinned trailer
        // read. The nested HSST inside the sub-tag value (slot-prefix → slot-suffix → value)
        // has a non-fixed layout, so the inner walk goes back through HsstReader's dispatch.
        if (!HsstDenseByteIndexReader.TryResolveSingleTag<TReader, TPin>(
                in reader, addressBound, PersistedSnapshotTags.SlotSubTagByte, out Bound slotSubTagBound) ||
            slotSubTagBound.Length == 0)
        {
            slotBound = default;
            return false;
        }
        Span<byte> slotKey = stackalloc byte[32];
        index.ToBigEndian(slotKey);
        using HsstReader<TReader, TPin> r = new(in reader, slotSubTagBound);
        if (!r.TrySeek(slotKey[..SlotPrefixLength], out _) ||
            !r.TrySeek(slotKey[SlotPrefixLength..], out _))
        {
            slotBound = default;
            return false;
        }
        slotBound = r.GetBound();
        return true;
    }

    internal static bool? TryGetSelfDestructFlag<TReader, TPin>(scoped in TReader reader, Bound addressBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        if (!HsstDenseByteIndexReader.TryResolveSingleTag<TReader, TPin>(
                in reader, addressBound, PersistedSnapshotTags.SelfDestructSubTagByte, out Bound b))
            return null;
        // length 0 = absent (DenseByteIndex gap fill). [0x00] = destructed. [0x01] = new account.
        if (b.Length == 0) return null;
        Span<byte> oneByte = stackalloc byte[1];
        if (!reader.TryRead(b.Offset, oneByte)) return null;
        return oneByte[0] != PersistedSnapshotTags.SelfDestructDestructedMarkerByte;
    }

    /// <summary>
    /// Look up a state-trie node by tree path. Returns the local value <see cref="Bound"/>
    /// — caller (<see cref="PersistedSnapshot"/>) checks <c>HasNodeRefs</c>, decodes the
    /// NodeRef when present, and does the cross-snapshot dereference.
    /// </summary>
    internal static bool TryLoadStateNodeRlp<TReader, TPin>(scoped in TReader reader, scoped in TreePath path, out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        if (path.Length <= TopPathThreshold)
        {
            Span<byte> key = stackalloc byte[4];
            path.EncodeWith4Byte(key);
            return TryGetFromColumn<TReader, TPin>(in reader, PersistedSnapshotTags.StateTopNodesTag, key, out bound);
        }
        if (path.Length <= CompactPathThreshold)
        {
            Span<byte> key = stackalloc byte[8];
            path.EncodeWith8Byte(key);
            return TryGetFromColumn<TReader, TPin>(in reader, PersistedSnapshotTags.StateNodeTag, key, out bound);
        }
        Span<byte> fullKey = stackalloc byte[33];
        path.Path.Bytes.CopyTo(fullKey);
        fullKey[32] = (byte)path.Length;
        return TryGetFromColumn<TReader, TPin>(in reader, PersistedSnapshotTags.StateNodeFallbackTag, fullKey, out bound);
    }

    /// <summary>
    /// Look up a storage-trie node within an already-positioned per-address inner HSST
    /// (produced by <see cref="TryGetAddressHsstBound"/> and cached on the snapshot).
    /// Walks sub-tag <c>StorageTopSubTag</c> for top paths (length 0-7),
    /// <c>StorageCompactSubTag</c> for compact paths (length 8-15), and
    /// <c>StorageFallbackSubTag</c> for paths past the compact threshold.
    /// </summary>
    internal static bool TryLoadStorageNodeRlpInBound<TReader, TPin>(scoped in TReader reader, Bound addressBound, in TreePath path, out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        // Per-address sub-tag step is always DenseByteIndex — resolve in one pinned trailer
        // read. The nested HSST inside the sub-tag value (TreePath → NodeRef) has a non-fixed
        // layout, so the inner walk goes back through HsstReader's dispatch. DenseByteIndex
        // returns success even for gap-filled (length 0) absences; treat length 0 as "no
        // entry for this sub-tag" so callers don't read into the adjacent sub-tag bytes.
        byte subTag;
        int keyLen;
        if (path.Length <= TopPathThreshold) { subTag = PersistedSnapshotTags.StorageTopSubTagByte; keyLen = 4; }
        else if (path.Length <= CompactPathThreshold) { subTag = PersistedSnapshotTags.StorageCompactSubTagByte; keyLen = 8; }
        else { subTag = PersistedSnapshotTags.StorageFallbackSubTagByte; keyLen = 33; }

        if (!HsstDenseByteIndexReader.TryResolveSingleTag<TReader, TPin>(
                in reader, addressBound, subTag, out Bound subTagBound) ||
            subTagBound.Length == 0)
        {
            bound = default;
            return false;
        }

        Span<byte> key = stackalloc byte[33];
        Span<byte> keySlice = key[..keyLen];
        switch (keyLen)
        {
            case 4: path.EncodeWith4Byte(keySlice); break;
            case 8: path.EncodeWith8Byte(keySlice); break;
            default:
                path.Path.Bytes.CopyTo(keySlice);
                keySlice[32] = (byte)path.Length;
                break;
        }

        using HsstReader<TReader, TPin> r = new(in reader, subTagBound);
        if (!r.TrySeek(keySlice, out _))
        {
            bound = default;
            return false;
        }
        bound = r.GetBound();
        if (bound.Length == 0) { bound = default; return false; }
        return true;
    }

    internal static ushort[]? ReadRefIdsFromMetadata<TReader, TPin>(scoped in TReader reader)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshotTags.MetadataTag, out _) ||
            !r.TrySeek(PersistedSnapshotTags.MetadataRefIdsKey, out _))
            return null;
        Bound b = r.GetBound();
        if (b.Length == 0 || b.Length % 2 != 0) return null;
        int len = checked((int)b.Length);
        int count = len / 2;
        Span<byte> buf = stackalloc byte[256];
        if (len > buf.Length)
            buf = new byte[len];
        if (!reader.TryRead(b.Offset, buf[..len])) return null;
        ushort[] ids = new ushort[count];
        for (int i = 0; i < count; i++)
            ids[i] = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(i * 2, 2));
        return ids;
    }

    private static bool TryGetFromColumn<TReader, TPin>(in TReader reader, scoped ReadOnlySpan<byte> tag, scoped ReadOnlySpan<byte> entityKey, out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(tag, out _) || !r.TrySeek(entityKey, out _))
        {
            bound = default;
            return false;
        }
        bound = r.GetBound();
        return true;
    }

    internal static TreePath DecodeCompactTreePath(ReadOnlySpan<byte> key) =>
        TreePath.DecodeWith8Byte(key);
}
