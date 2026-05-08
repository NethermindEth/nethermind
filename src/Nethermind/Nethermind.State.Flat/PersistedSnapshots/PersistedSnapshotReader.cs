// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    private const int TopPathThreshold = 5;
    private const int CompactPathThreshold = 15;
    private const int StorageHashPrefixLength = 20;
    private const int SlotPrefixLength = 31;

    /// <summary>
    /// Seek the per-address inner-HSST bound:
    /// AccountColumnTag → addressHash.Bytes[..StorageHashPrefixLength].
    /// On success outs the inner-HSST bound that <see cref="HsstReader{TReader,TPin}"/>
    /// can be re-entered with to do sub-tag lookups (account, slots, self-destruct,
    /// storage trie) without re-walking the outer column. Used by
    /// <see cref="PersistedSnapshot"/> to populate its address-hash→bound LRU.
    /// </summary>
    internal static bool TryGetAddressHsstBound<TReader, TPin>(scoped in TReader reader, in ValueHash256 addressHash, out Bound addressBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ||
            !r.TrySeek(addressHash.Bytes[..StorageHashPrefixLength], out _))
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
        using HsstReader<TReader, TPin> r = new(in reader, addressBound);
        // DenseByteIndex returns success for any tag below count, including gap-filled
        // (length 0) absences; treat length 0 as "no account record" so callers don't
        // misread an absent entry as a deleted account.
        if (!r.TrySeek(PersistedSnapshot.AccountSubTag, out _))
        {
            accountBound = default;
            return false;
        }
        Bound b = r.GetBound();
        if (b.Length == 0)
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
        using HsstReader<TReader, TPin> r = new(in reader, addressBound);
        Span<byte> slotKey = stackalloc byte[32];
        index.ToBigEndian(slotKey);
        if (!r.TrySeek(PersistedSnapshot.SlotSubTag, out _) ||
            !r.TrySeek(slotKey[..SlotPrefixLength], out _) ||
            !r.TrySeek(slotKey[SlotPrefixLength..], out _))
        {
            slotBound = default;
            return false;
        }
        slotBound = r.GetBound();
        return true;
    }

    internal static bool IsSelfDestructed<TReader, TPin>(scoped in TReader reader, Bound addressBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader, addressBound);
        // Presence-marker encoding: an entry of length 0 means "no SD record" (gap-filled
        // by DenseByteIndex); only a non-empty value (with marker [0x00]/[0x01]) counts.
        return r.TrySeek(PersistedSnapshot.SelfDestructSubTag, out _) && r.GetBound().Length > 0;
    }

    internal static bool? TryGetSelfDestructFlag<TReader, TPin>(scoped in TReader reader, Bound addressBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader, addressBound);
        if (!r.TrySeek(PersistedSnapshot.SelfDestructSubTag, out _))
            return null;
        Bound b = r.GetBound();
        // length 0 = absent (DenseByteIndex gap fill). [0x00] = destructed. [0x01] = new account.
        if (b.Length == 0) return null;
        Span<byte> oneByte = stackalloc byte[1];
        if (!reader.TryRead(b.Offset, oneByte)) return null;
        return oneByte[0] != 0x00;
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
            Span<byte> key = stackalloc byte[3];
            path.EncodeWith3Byte(key);
            return TryGetFromColumn<TReader, TPin>(in reader, PersistedSnapshot.StateTopNodesTag, key, out bound);
        }
        if (path.Length <= CompactPathThreshold)
        {
            Span<byte> key = stackalloc byte[8];
            path.EncodeWith8Byte(key);
            return TryGetFromColumn<TReader, TPin>(in reader, PersistedSnapshot.StateNodeTag, key, out bound);
        }
        Span<byte> fullKey = stackalloc byte[33];
        path.Path.Bytes.CopyTo(fullKey);
        fullKey[32] = (byte)path.Length;
        return TryGetFromColumn<TReader, TPin>(in reader, PersistedSnapshot.StateNodeFallbackTag, fullKey, out bound);
    }

    /// <summary>
    /// Look up a storage-trie node within an already-positioned per-address inner HSST
    /// (produced by <see cref="TryGetAddressHsstBound"/> and cached on the snapshot).
    /// Walks sub-tag <c>StorageTopSubTag</c> for top paths (length 0-5),
    /// <c>StorageCompactSubTag</c> for compact paths (length 6-15), and
    /// <c>StorageFallbackSubTag</c> for paths past the compact threshold.
    /// </summary>
    internal static bool TryLoadStorageNodeRlpInBound<TReader, TPin>(scoped in TReader reader, Bound addressBound, in TreePath path, out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader, addressBound);
        if (path.Length <= TopPathThreshold)
        {
            Span<byte> key = stackalloc byte[3];
            path.EncodeWith3Byte(key);
            if (!r.TrySeek(PersistedSnapshot.StorageTopSubTag, out _) ||
                !r.TrySeek(key, out _))
            {
                bound = default;
                return false;
            }
            bound = r.GetBound();
            if (bound.Length == 0) { bound = default; return false; }
            return true;
        }
        if (path.Length <= CompactPathThreshold)
        {
            Span<byte> key = stackalloc byte[8];
            path.EncodeWith8Byte(key);
            if (!r.TrySeek(PersistedSnapshot.StorageCompactSubTag, out _) ||
                !r.TrySeek(key, out _))
            {
                bound = default;
                return false;
            }
            bound = r.GetBound();
            // DenseByteIndex returns success even for gap-filled (length 0) absences; treat
            // length 0 as "no compact entry for this path" so callers don't read into the
            // adjacent fallback sub-tag value bytes by mistake.
            if (bound.Length == 0) { bound = default; return false; }
            return true;
        }
        Span<byte> fullKey = stackalloc byte[33];
        path.Path.Bytes.CopyTo(fullKey);
        fullKey[32] = (byte)path.Length;
        if (!r.TrySeek(PersistedSnapshot.StorageFallbackSubTag, out _) ||
            !r.TrySeek(fullKey, out _))
        {
            bound = default;
            return false;
        }
        bound = r.GetBound();
        if (bound.Length == 0) { bound = default; return false; }
        return true;
    }

    internal static bool CheckHasNodeRefsFlag<TReader, TPin>(scoped in TReader reader)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        return r.TrySeek(PersistedSnapshot.MetadataTag, out _)
            && r.TrySeek("noderefs"u8, out _);
    }

    internal static int[]? ReadRefIdsFromMetadata<TReader, TPin>(scoped in TReader reader)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshot.MetadataTag, out _) ||
            !r.TrySeek("ref_ids"u8, out _))
            return null;
        Bound b = r.GetBound();
        if (b.Length == 0 || b.Length % 4 != 0) return null;
        int len = checked((int)b.Length);
        int count = len / 4;
        Span<byte> buf = stackalloc byte[256];
        if (len > buf.Length)
            buf = new byte[len];
        if (!reader.TryRead(b.Offset, buf[..len])) return null;
        int[] ids = new int[count];
        for (int i = 0; i < count; i++)
            ids[i] = BitConverter.ToInt32(buf.Slice(i * 4, 4));
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

    /// <summary>
    /// Pre-touch outer column 0x01's BTree index nodes (the address-hash directory)
    /// through the standard reader so each touched page is registered with the
    /// arena's <see cref="PageResidencyTracker"/>. Caller is expected to have just
    /// dropped the snapshot pages via <c>AdviseDontNeed</c>; this brings the index
    /// region back warm without touching the per-address inner-HSST data region.
    /// </summary>
    /// <remarks>
    /// Column 0x01 uses the BTree HSST layout (<c>[Data Region][Index Region][IndexType]</c>),
    /// which has no length-of-data-region field — the data/index split can only be
    /// discovered by walking the tree. So this DFS-walks every BTree node via
    /// <see cref="HsstBTreeReader.TryLoadNode{TReader,TPin}"/>, whose <c>PinBuffer</c>
    /// reads are what register pages with the tracker. Leaf entries are *not*
    /// visited — visiting them would pin into the data region and warm pages that
    /// belong to per-address inner HSSTs.
    /// </remarks>
    internal static void WarmAddressIndex<TReader, TPin>(scoped in TReader reader)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        Bound col;
        using (HsstReader<TReader, TPin> outer = new(in reader))
        {
            if (!outer.TrySeek(PersistedSnapshot.AccountColumnTag, out _)) return;
            col = outer.GetBound();
        }
        if (col.Length < 3 + 12) return;

        // BTree trailer is [RootSize u16 LE][IndexType u8]; root starts at scopeEnd - 3 - rootSize.
        Span<byte> sizeBuf = stackalloc byte[2];
        if (!reader.TryRead(col.Offset + col.Length - 3, sizeBuf)) return;
        int rootSize = sizeBuf[0] | (sizeBuf[1] << 8);
        long rootAbsStart = col.Offset + col.Length - 3 - rootSize;
        long scopeEnd = col.Offset + col.Length - 3;
        WalkBTreeIndexNodes<TReader, TPin>(in reader, col, rootAbsStart, scopeEnd);
    }

    private static void WalkBTreeIndexNodes<TReader, TPin>(
        scoped in TReader reader, Bound scope, long absStart, long scopeEnd)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        if (!HsstBTreeReader.TryLoadNode<TReader, TPin>(in reader, absStart, scopeEnd,
                out HsstIndex node, out TPin pin))
            return;
        using (pin)
        {
            // Leaf already faulted in by TryLoadNode's PinBuffer; do not descend
            // into entries (their metaStart pointers sit in the data region).
            if (!node.IsIntermediate) return;
            // Phantom slot 0 dropped: leftmost child sits at BaseOffset; the
            // remaining N-1 children encode as deltas in the value array.
            long leftmostRel = (long)node.Metadata.BaseOffset;
            WalkBTreeIndexNodes<TReader, TPin>(
                in reader, scope, scope.Offset + leftmostRel, scopeEnd);
            int n = node.EntryCount;
            for (int i = 0; i < n; i++)
            {
                long childRelStart = (long)node.GetUInt64Value(i);
                WalkBTreeIndexNodes<TReader, TPin>(
                    in reader, scope, scope.Offset + childRelStart, scopeEnd);
            }
        }
    }
}
