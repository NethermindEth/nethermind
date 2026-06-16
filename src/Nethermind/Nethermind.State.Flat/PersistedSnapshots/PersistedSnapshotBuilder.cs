// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using Nethermind.State.Flat.Hsst.BTree;
using Nethermind.State.Flat.Hsst.DenseByteIndex;
using Nethermind.State.Flat.Hsst.TwoByteSlot;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Builds columnar HSST byte data from an in-memory <see cref="Snapshot"/>. All
/// persisted snapshots are blob-backed: trie-node RLP values are stored as
/// <see cref="NodeRef"/>s pointing into blob arenas, while account / slot /
/// self-destruct values are inlined in the metadata HSST.
///
/// The outer HSST has 6 column entries, each containing an inner HSST. Inner HSST
/// keys are the entity keys without the tag prefix. The per-address column (0x01)
/// is keyed by raw 20-byte Address; the storage-trie column (0x05) is keyed by
/// 20-byte addressHash prefix.
/// </summary>
public static class PersistedSnapshotBuilder
{
    private const int TopPathThreshold = 7;
    private const int CompactPathThreshold = 15;

    private static readonly Comparison<TreePath> StateNodeComparer = (a, b) =>
    {
        int cmp = a.Path.Bytes.SequenceCompareTo(b.Path.Bytes);
        return cmp != 0 ? cmp : a.Length.CompareTo(b.Length);
    };

    // Sorts storage-trie node keys by 20-byte address-hash prefix (matching the column-0x05
    // outer key) and then by encoded path so per-addressHash slices are contiguous and the
    // inner HSST keys are in sorted order.
    private static readonly Comparison<(ValueHash256 AddrHash, TreePath Path)> StorageNodeComparer = (a, b) =>
    {
        int cmp = a.AddrHash.Bytes[..PersistedSnapshotTags.AddressHashPrefixLength].SequenceCompareTo(b.AddrHash.Bytes[..PersistedSnapshotTags.AddressHashPrefixLength]);
        if (cmp != 0) return cmp;
        cmp = a.Path.Path.Bytes.SequenceCompareTo(b.Path.Path.Bytes);
        return cmp != 0 ? cmp : a.Path.Length.CompareTo(b.Path.Length);
    };

    // Sorts slot entries by raw Address bytes (matching the column-0x01 outer key) then
    // by slot value, so per-address slices are contiguous and slot keys within a slice
    // are in sorted big-endian order.
    private static readonly Comparison<((ValueAddress Addr, UInt256 Slot) Key, SlotValue? Value)> StoragesByAddressComparer = (a, b) =>
    {
        int cmp = a.Key.Addr.AsSpan.SequenceCompareTo(b.Key.Addr.AsSpan);
        if (cmp != 0) return cmp;
        return a.Key.Slot.CompareTo(b.Key.Slot);
    };

    private static readonly Comparison<ValueAddress> ValueAddressComparer = (a, b) =>
        a.AsSpan.SequenceCompareTo(b.AsSpan);

    public static void Build<TWriter>(Snapshot snapshot, ref TWriter writer, BlobArenaWriter blobWriter, BloomFilter bloom) where TWriter : IByteBufferWriter
    {
        // To stay off the LOH, we keep only the unmanaged sort keys in NativeMemoryList
        // (off-heap) and re-fetch the TrieNode value from the source ConcurrentDictionary
        // at column-write time. PooledSet is used for the small Address dedup map so its
        // backing entry array is pool-rented rather than freshly allocated each block.
        NativeMemoryList<TreePath> stateTopKeys = null!, stateCompactKeys = null!, stateFallbackKeys = null!;
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storTopKeys = null!, storCompactKeys = null!, storFallbackKeys = null!;
        NativeMemoryList<((ValueAddress Addr, UInt256 Slot) Key, SlotValue? Value)> sortedStorages = null!;
        NativeMemoryList<ValueAddress> uniqueAddresses = null!;

        // Parallel extraction + sort: three independent jobs over disjoint dictionaries.
        Parallel.Invoke(
            () =>
            {
                // Job A: state trie nodes — partition keys into top/compact/fallback, then
                // sort. TrieNode values stay in snapshot.StateNodes; we re-fetch at write
                // time. IsPersisted / prune mutations happen here while we still have the
                // value in hand.
                NativeMemoryList<TreePath> top = new(0);
                NativeMemoryList<TreePath> compact = new(snapshot.StateNodesCount);
                NativeMemoryList<TreePath> fallback = new(0);
                foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes)
                {
                    if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
                    TreePath path = kv.Key;
                    if (path.Length <= TopPathThreshold) top.Add(path);
                    else if (path.Length <= CompactPathThreshold) compact.Add(path);
                    else fallback.Add(path);
                    kv.Value.IsPersisted = true;
                    kv.Value.PrunePersistedRecursively(1);
                }
                Parallel.Invoke(
                    () => top.Sort(StateNodeComparer),
                    () => compact.Sort(StateNodeComparer),
                    () => fallback.Sort(StateNodeComparer));
                stateTopKeys = top; stateCompactKeys = compact; stateFallbackKeys = fallback;
            },
            () =>
            {
                // Job B: storage trie nodes (column 0x05) — store (ValueHash256, TreePath)
                // keys off-heap. Column writers materialize a fresh Hash256 from the value
                // hash on demand (one Gen0 alloc per addressHash that has storage-trie
                // nodes) for the snapshot.TryGetStorageNode lookup.
                NativeMemoryList<(ValueHash256, TreePath)> top = new(0);
                NativeMemoryList<(ValueHash256, TreePath)> compact = new(snapshot.StorageNodesCount);
                NativeMemoryList<(ValueHash256, TreePath)> fallback = new(0);
                foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kv in snapshot.StorageNodes)
                {
                    if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
                    (Hash256 addr, TreePath path) = kv.Key.Key;
                    ValueHash256 addrHash = addr.ValueHash256;
                    if (path.Length <= TopPathThreshold) top.Add((addrHash, path));
                    else if (path.Length <= CompactPathThreshold) compact.Add((addrHash, path));
                    else fallback.Add((addrHash, path));
                    kv.Value.IsPersisted = true;
                    kv.Value.PrunePersistedRecursively(1);
                }
                Parallel.Invoke(
                    () => top.Sort(StorageNodeComparer),
                    () => compact.Sort(StorageNodeComparer),
                    () => fallback.Sort(StorageNodeComparer));
                storTopKeys = top; storCompactKeys = compact; storFallbackKeys = fallback;
            },
            () =>
            {
                // Job C: account column prep — collect raw-Address-keyed sources (accounts /
                // SD / slots), sort by raw bytes. No hashing — column 0x01 is keyed by raw
                // Address, and storage-trie addresses live in column 0x05 keyed by addressHash
                // (handled separately by Job B's outputs).
                using PooledSet<HashedKey<Address>> seen = new();
                foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts)
                    seen.Add(kv.Key);
                foreach (KeyValuePair<HashedKey<Address>, bool> kv in snapshot.SelfDestructedStorageAddresses)
                    seen.Add(kv.Key);

                NativeMemoryList<((ValueAddress Addr, UInt256 Slot) Key, SlotValue? Value)> storages =
                    new(Math.Max(1, snapshot.StoragesCount));
                foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> kv in snapshot.Storages)
                {
                    (Address addr, UInt256 slot) = kv.Key.Key;
                    storages.Add(((new ValueAddress(addr.Bytes), slot), kv.Value));
                    seen.Add(addr);
                }

                NativeMemoryList<ValueAddress> addresses = new(Math.Max(1, seen.Count));
                foreach (HashedKey<Address> addr in seen)
                    addresses.Add(new ValueAddress(addr.Key.Bytes));
                addresses.Sort(ValueAddressComparer);

                storages.Sort(StoragesByAddressComparer);

                sortedStorages = storages;
                uniqueAddresses = addresses;
            });

        HsstDenseByteIndexBuilder<TWriter> outer = new(ref writer);
        try
        {
            // Columns are emitted in strictly descending tag order, as the outer
            // DenseByteIndex requires (writer streams high-tag → low-tag so the
            // small/hot Metadata column ends up adjacent to the lookup table).

            // Column 0x05: Storage-trie per-addressHash column.
            WriteStorageTrieColumn<TWriter>(ref outer, snapshot, storTopKeys, storCompactKeys, storFallbackKeys, blobWriter, bloom);

            // Column 0x04: State nodes fallback (path length 16+)
            WriteStateNodesColumnFallback<TWriter>(ref outer, snapshot, stateFallbackKeys, blobWriter, bloom);

            // Column 0x03: State nodes (compact, path length 6-15)
            WriteStateNodesColumnCompact<TWriter>(ref outer, snapshot, stateCompactKeys, blobWriter, bloom);

            // Column 0x02: State top nodes (path length 0-5)
            WriteStateTopNodesColumn<TWriter>(ref outer, snapshot, stateTopKeys, blobWriter, bloom);

            // Column 0x01: Per-address column keyed by raw Address. Inner sub-tags
            // 0x00..0x02 cover account RLP, self-destruct, and slots.
            WritePerAddressColumn<TWriter>(ref outer, snapshot, sortedStorages, uniqueAddresses, blobWriter, bloom);

            WriteMetadataColumn<TWriter>(ref outer, snapshot, blobWriter);

            outer.Build();
        }
        finally
        {
            outer.Dispose();
            sortedStorages?.Dispose();
            uniqueAddresses?.Dispose();
            stateTopKeys?.Dispose();
            stateCompactKeys?.Dispose();
            stateFallbackKeys?.Dispose();
            storTopKeys?.Dispose();
            storCompactKeys?.Dispose();
            storFallbackKeys?.Dispose();
        }
    }

    /// <summary>
    /// Estimate of the serialized snapshot size, used to size the destination arena
    /// reservation. Capped at 2 GiB — the hard ceiling on a Full snapshot — which also
    /// keeps the value within <see cref="int"/>.MaxValue for contiguous-buffer callers.
    /// </summary>
    public static long EstimateSize(Snapshot snapshot) =>
        Math.Min(2.GiB, snapshot.EstimateMemory() + 1.KiB);

    private static void WriteMetadataColumn<TWriter>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot, BlobArenaWriter blobWriter) where TWriter : IByteBufferWriter
    {
        // Metadata keys must be in sorted ASCII order:
        // "blob_range" < "from_block" < "from_hash" < "ref_ids" < "to_block" < "to_hash" < "version"
        // blob_range is this base snapshot's contiguous trie-RLP run in the single blob arena
        // it targeted — every column above wrote through this same blobWriter, so the run is
        // final here (the last column written). ref_ids carries this snapshot's referenced
        // blob arena id(s). For a freshly built base snapshot it's a single int — the id of
        // the blob arena the builder just wrote its trie RLPs into. Compactor's
        // NWayMetadataMerge replaces this with the union of input snapshots' referenced ids
        // and emits noderefs instead of blob_range.
        BlobRange blobRange = blobWriter.Written > blobWriter.StartOffset
            ? new BlobRange(blobWriter.BlobArenaId, blobWriter.StartOffset, blobWriter.Written - blobWriter.StartOffset)
            : BlobRange.None;

        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilderBuffers.Container innerBuffers = new(expectedKeyCount: 7);
        using HsstBTreeBuilder<TWriter> inner = new(ref innerWriter, ref innerBuffers.Buffers, PersistedSnapshotTags.MetadataKeyLength, expectedKeyCount: 7);

        Span<byte> blockNumBytes = stackalloc byte[8];
        Span<byte> refIdsBytes = stackalloc byte[2];
        Span<byte> blobRangeBytes = stackalloc byte[BlobRange.SerializedSize];

        blobRange.Write(blobRangeBytes);
        inner.Add(PersistedSnapshotTags.MetadataBlobRangeKey, blobRangeBytes);

        BitConverter.TryWriteBytes(blockNumBytes, snapshot.From.BlockNumber);
        inner.Add(PersistedSnapshotTags.MetadataFromBlockKey, blockNumBytes);

        inner.Add(PersistedSnapshotTags.MetadataFromHashKey, snapshot.From.StateRoot.Bytes);

        BinaryPrimitives.WriteUInt16LittleEndian(refIdsBytes, blobWriter.BlobArenaId);
        inner.Add(PersistedSnapshotTags.MetadataRefIdsKey, refIdsBytes);

        BitConverter.TryWriteBytes(blockNumBytes, snapshot.To.BlockNumber);
        inner.Add(PersistedSnapshotTags.MetadataToBlockKey, blockNumBytes);

        inner.Add(PersistedSnapshotTags.MetadataToHashKey, snapshot.To.StateRoot.Bytes);

        inner.Add(PersistedSnapshotTags.MetadataVersionKey, PersistedSnapshotTags.MetadataFormatVersion);

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshotTags.MetadataTag);
    }

    private static void WritePerAddressColumn<TWriter>(
        ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot,
        NativeMemoryList<((ValueAddress Addr, UInt256 Slot) Key, SlotValue? Value)> sortedStorages,
        NativeMemoryList<ValueAddress> uniqueAddresses,
        BlobArenaWriter blobWriter,
        BloomFilter bloom) where TWriter : IByteBufferWriter
    {
        const int slotPrefixLength = 30;
        const int slotSuffixLength = 32 - slotPrefixLength;

        ref TWriter addressWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilderBuffers.Container addressLevelBuffers = new(expectedKeyCount: uniqueAddresses.Count);
        using HsstBTreeBuilder<TWriter> addressLevel = new(ref addressWriter, ref addressLevelBuffers.Buffers, PersistedSnapshotTags.AddressKeyLength, expectedKeyCount: uniqueAddresses.Count);
        // Slim-account RLP fits in 256 bytes; pool the scratch to avoid per-call allocation.
        byte[] rlpBuffer = ArrayPool<byte>.Shared.Rent(256);
        RlpStream rlpStream = new(rlpBuffer);
        Span<byte> slotKey = stackalloc byte[32];
        Span<byte> currentPrefixBuf = stackalloc byte[slotPrefixLength];
        // Reused across the address loop to avoid ArrayPool/NativeMemory churn per slot subtree.
        using HsstBTreeBuilderBuffers.Container slotPrefixBuffers = new();

        // The slot-prefix BTree is key-first ([FullKey][LEB128][Value]), so the value length
        // must be known before the LEB128 — stage the sub-slot bytes in full first. Reset()
        // between iterations amortizes the NativeMemory allocation across the loops.
        using PooledByteBufferWriter slotSuffixBuffer = new(4096);
        // No-slots fast path: stage the bounded per-address inner HSST ({SD, Account} +
        // trailer, well under 256 bytes) so the outer value length is known up-front and
        // addressLevel.Add can apply its 4 KiB page-alignment pad, keeping each EOA's blob
        // on a single OS page.
        using PooledByteBufferWriter noStorageBuffer = new(256);
        int storageIdx = 0;

        for (int addrIdx = 0; addrIdx < uniqueAddresses.Count; addrIdx++)
        {
            ValueAddress addrValue = uniqueAddresses[addrIdx];
            ReadOnlySpan<byte> addressBytes = addrValue.AsSpan;
            Address address = addrValue.ToAddress();

            ulong addrBloomKey = PersistedSnapshotBloomBuilder.AddressKey(addressBytes);
            bloom.Add(addrBloomKey);

            bool hasSlots = storageIdx < sortedStorages.Count &&
                sortedStorages[storageIdx].Key.Addr.AsSpan.SequenceEqual(addressBytes);
            if (!hasSlots)
            {
                noStorageBuffer.Reset();
                ref PooledByteBufferWriter.Writer stagingWriter = ref noStorageBuffer.GetWriter();
                using (HsstDenseByteIndexBuilder<PooledByteBufferWriter.Writer> stagedPerAddr = new(ref stagingWriter))
                {
                    if (snapshot.Content.SelfDestructedStorageAddresses.TryGetValue(address, out bool stagedSdValue))
                        stagedPerAddr.Add(PersistedSnapshotTags.SelfDestructSubTag,
                            stagedSdValue ? PersistedSnapshotTags.SelfDestructNewMarker : PersistedSnapshotTags.SelfDestructDestructedMarker);

                    if (snapshot.TryGetAccount(address, out Account? stagedAccount))
                    {
                        if (stagedAccount is null)
                        {
                            stagedPerAddr.Add(PersistedSnapshotTags.AccountSubTag, PersistedSnapshotTags.AccountDeletedMarker);
                        }
                        else
                        {
                            int len = AccountDecoder.Slim.GetLength(stagedAccount);
                            rlpStream.Reset();
                            AccountDecoder.Slim.Encode(rlpStream, stagedAccount);
                            stagedPerAddr.Add(PersistedSnapshotTags.AccountSubTag, rlpBuffer.AsSpan(0, len));
                        }
                    }

                    stagedPerAddr.Build();
                }

                addressLevel.Add(addressBytes, noStorageBuffer.WrittenSpan);
                continue;
            }

            // Begin per-address HSST. Up to 3 sub-tags 0x00..0x02 written in strictly
            // descending tag order (DenseByteIndex contract); the writer streams high-tag
            // entries first so the small/hot Account blob (sub-tag 0x00, written last)
            // lands adjacent to the trailing Ends[] table. Sub-tag value-presence semantics:
            //   0x02 slots: nested HSST(SlotPrefix(30) → nested HSST(SlotSuffix(2) → bytes))
            //   0x01 SD: [] absent / [0x00] destructed / [0x01] new account
            //   0x00 account: [] absent / [0x00] deleted / RLP-bytes present
            ref TWriter perAddrWriter = ref addressLevel.BeginValueWrite();
            long perAddrValueStart = perAddrWriter.Written;
            using HsstDenseByteIndexBuilder<TWriter> perAddr = new(ref perAddrWriter);

            // Sub-tag 0x02: Slots. Emitted first so the per-address DenseByteIndex receives
            // tags in strictly descending order.
            {
                ref TWriter slotWriter = ref perAddr.BeginValueWrite();
                using HsstBTreeBuilder<TWriter> prefixLevel = new(ref slotWriter, ref slotPrefixBuffers.Buffers, slotPrefixLength, keyFirst: true);

                while (storageIdx < sortedStorages.Count &&
                    sortedStorages[storageIdx].Key.Addr.AsSpan.SequenceEqual(addressBytes))
                {
                    sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                    slotKey[..slotPrefixLength].CopyTo(currentPrefixBuf);
                    ReadOnlySpan<byte> currentPrefix = currentPrefixBuf;

                    // Look ahead over the current prefix group to total its value bytes so we
                    // can pick offsetSize (2 = u16, 3 = u24) before writing the key-first entry.
                    // In practice, per-prefix groups are tiny so the look-ahead is cheap and
                    // the u16 cap is virtually never hit.
                    int groupStart = storageIdx;
                    int groupEnd = groupStart;
                    long groupValueBytes = 0;
                    while (groupEnd < sortedStorages.Count &&
                        sortedStorages[groupEnd].Key.Addr.AsSpan.SequenceEqual(addressBytes))
                    {
                        sortedStorages[groupEnd].Key.Slot.ToBigEndian(slotKey);
                        if (!slotKey[..slotPrefixLength].SequenceEqual(currentPrefix))
                            break;
                        SlotValue? v = sortedStorages[groupEnd].Value;
                        groupValueBytes += v.HasValue ? Rlp.LengthOf(v.Value.AsReadOnlySpan.WithoutLeadingZeros()) : 0;
                        groupEnd++;
                    }

                    slotSuffixBuffer.Reset();
                    ref PooledByteBufferWriter.Writer suffixWriter = ref slotSuffixBuffer.GetWriter();
                    // u16 offsets cap the data region at ushort.MaxValue; widen to u24
                    // (offsetSize: 3) when a group's payload overflows.
                    int suffixOffsetSize = HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(groupValueBytes) ? 2 : 3;
                    using (HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> suffixLevel = new(ref suffixWriter, suffixOffsetSize))
                    {
                        for (int i = groupStart; i < groupEnd; i++)
                        {
                            sortedStorages[i].Key.Slot.ToBigEndian(slotKey);
                            bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, slotKey));
                            SlotValue? value = sortedStorages[i].Value;
                            ReadOnlySpan<byte> suffixKey = slotKey.Slice(slotPrefixLength, slotSuffixLength);
                            // Present values are RLP-wrapped (≥ 1 byte even for zero → 0x80); null/deleted
                            // slots keep an empty payload so the length-0 = absent sentinel survives wrapping.
                            // Reuses the method-level rlpBuffer (free here; account RLP is written later).
                            ReadOnlySpan<byte> payload = value.HasValue
                                ? rlpBuffer.AsSpan(0, Rlp.Encode(value.Value.AsReadOnlySpan.WithoutLeadingZeros(), rlpBuffer))
                                : [];
                            suffixLevel.Add(suffixKey, payload);
                        }
                        suffixLevel.Build();
                    }
                    storageIdx = groupEnd;
                    prefixLevel.Add(currentPrefix, slotSuffixBuffer.WrittenSpan);
                }

                prefixLevel.Build();
                perAddr.FinishValueWrite(PersistedSnapshotTags.SlotSubTag);
            }

            if (snapshot.Content.SelfDestructedStorageAddresses.TryGetValue(address, out bool sdValue))
            {
                perAddr.Add(PersistedSnapshotTags.SelfDestructSubTag,
                    sdValue ? PersistedSnapshotTags.SelfDestructNewMarker : PersistedSnapshotTags.SelfDestructDestructedMarker);
            }

            // Sub-tag 0x00: slim account RLP starts with a list header (0xc0+), so the
            // [0x00] deleted-marker is unambiguous against any valid RLP encoding.
            if (snapshot.TryGetAccount(address, out Account? account))
            {
                if (account is null)
                {
                    perAddr.Add(PersistedSnapshotTags.AccountSubTag, PersistedSnapshotTags.AccountDeletedMarker);
                }
                else
                {
                    int len = AccountDecoder.Slim.GetLength(account);
                    rlpStream.Reset();
                    AccountDecoder.Slim.Encode(rlpStream, account);
                    perAddr.Add(PersistedSnapshotTags.AccountSubTag, rlpBuffer.AsSpan(0, len));
                }
            }

            perAddr.Build();
            addressLevel.FinishValueWrite(addressBytes, perAddrWriter.Written - perAddrValueStart);
        }

        addressLevel.Build();
        outer.FinishValueWrite(PersistedSnapshotTags.AccountColumnTag);
        ArrayPool<byte>.Shared.Return(rlpBuffer);
    }

    private static void WriteStorageTrieColumn<TWriter>(
        ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storTop,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storCompact,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storFallback,
        BlobArenaWriter blobWriter,
        BloomFilter bloom) where TWriter : IByteBufferWriter
    {
        // Build a deduped, sorted list of addressHashes that have at least one storage-trie
        // node. The three partitions are each already sorted by addressHash prefix → path;
        // we append the prefixes and run a sort-then-linear-dedupe over the full ValueHash256,
        // which is a strict refinement of the 20-byte prefix order the column key requires.
        int capacity = storTop.Count + storCompact.Count + storFallback.Count;
        using NativeMemoryListRef<ValueHash256> uniqueAddrHashes = new(Math.Max(1, capacity));
        for (int i = 0; i < storTop.Count; i++) uniqueAddrHashes.Add(storTop[i].AddrHash);
        for (int i = 0; i < storCompact.Count; i++) uniqueAddrHashes.Add(storCompact[i].AddrHash);
        for (int i = 0; i < storFallback.Count; i++) uniqueAddrHashes.Add(storFallback[i].AddrHash);
        uniqueAddrHashes.Sort((a, b) => a.CompareTo(b));
        {
            Span<ValueHash256> span = uniqueAddrHashes.AsSpan();
            int write = 0;
            for (int read = 0; read < span.Length; read++)
            {
                if (write == 0 || !span[read].Equals(span[write - 1]))
                    span[write++] = span[read];
            }
            uniqueAddrHashes.Truncate(write);
        }

        ref TWriter colWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilderBuffers.Container addrLevelBuffers = new(expectedKeyCount: uniqueAddrHashes.Count);
        using HsstBTreeBuilder<TWriter> addrLevel = new(ref colWriter, ref addrLevelBuffers.Buffers, PersistedSnapshotTags.AddressHashPrefixLength, expectedKeyCount: uniqueAddrHashes.Count);

        Span<byte> topPathKey = stackalloc byte[4];
        Span<byte> compactPathKey = stackalloc byte[8];
        Span<byte> fallbackPathKey = stackalloc byte[33];
        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];

        int topIdx = 0, compactIdx = 0, fallbackIdx = 0;

        for (int i = 0; i < uniqueAddrHashes.Count; i++)
        {
            ValueHash256 addressHash = uniqueAddrHashes[i];
            ReadOnlySpan<byte> addressHashPrefix = addressHash.Bytes[..PersistedSnapshotTags.AddressHashPrefixLength];
            Hash256? addrRefForStorageNode = null;

            ref TWriter perAddrHashWriter = ref addrLevel.BeginValueWrite();
            long perAddrHashValueStart = perAddrHashWriter.Written;
            using HsstDenseByteIndexBuilder<TWriter> perAddrHash = new(ref perAddrHashWriter);

            // Sub-tag 0x02: Storage trie nodes (fallback, 33-byte path keys, length 16+).
            // Emitted first so the per-addressHash DenseByteIndex receives tags in strictly
            // descending order (0x02 > 0x01 > 0x00).
            int fallbackStart = fallbackIdx;
            while (fallbackIdx < storFallback.Count &&
                storFallback[fallbackIdx].AddrHash.Bytes[..PersistedSnapshotTags.AddressHashPrefixLength].SequenceEqual(addressHashPrefix))
                fallbackIdx++;
            if (fallbackStart < fallbackIdx)
            {
                addrRefForStorageNode ??= new Hash256(in addressHash);
                ref TWriter fbWriter = ref perAddrHash.BeginValueWrite();
                using HsstBTreeBuilderBuffers.Container fbBuffers = new(expectedKeyCount: fallbackIdx - fallbackStart);
                using HsstBTreeBuilder<TWriter> fbLevel = new(ref fbWriter, ref fbBuffers.Buffers, keyLength: 33, expectedKeyCount: fallbackIdx - fallbackStart);
                for (int j = fallbackStart; j < fallbackIdx; j++)
                {
                    (ValueHash256 _, TreePath path) = storFallback[j];
                    snapshot.TryGetStorageNode((addrRefForStorageNode, path), out TrieNode? node);
                    path.Path.Bytes.CopyTo(fallbackPathKey);
                    fallbackPathKey[32] = (byte)path.Length;
                    ReadOnlySpan<byte> fbRlp = node!.FullRlp.AsSpan();
                    NodeRef fbNr = blobWriter.WriteRlp(fbRlp);
                    NodeRef.Write(nrBuf, in fbNr);
                    ref TWriter fbValueWriter = ref fbLevel.BeginValueWrite();
                    IByteBufferWriter.Copy(ref fbValueWriter, nrBuf);
                    fbLevel.FinishValueWrite(fallbackPathKey, NodeRef.Size);
                    bloom.Add(PersistedSnapshotBloomBuilder.StorageNodeKey(in addressHash, in path));
                }
                fbLevel.Build();
                perAddrHash.FinishValueWrite(PersistedSnapshotTags.StorageFallbackSubTag);
            }

            // Sub-tag 0x01: Storage trie nodes (compact, 8-byte path keys, length 6-15).
            int compactStart = compactIdx;
            while (compactIdx < storCompact.Count &&
                storCompact[compactIdx].AddrHash.Bytes[..PersistedSnapshotTags.AddressHashPrefixLength].SequenceEqual(addressHashPrefix))
                compactIdx++;
            if (compactStart < compactIdx)
            {
                addrRefForStorageNode ??= new Hash256(in addressHash);
                ref TWriter compactWriter = ref perAddrHash.BeginValueWrite();
                using HsstBTreeBuilderBuffers.Container compactBuffers = new(expectedKeyCount: compactIdx - compactStart);
                using HsstBTreeBuilder<TWriter> compactLevel = new(ref compactWriter, ref compactBuffers.Buffers, keyLength: 8,
                    expectedKeyCount: compactIdx - compactStart);
                for (int j = compactStart; j < compactIdx; j++)
                {
                    (ValueHash256 _, TreePath path) = storCompact[j];
                    snapshot.TryGetStorageNode((addrRefForStorageNode, path), out TrieNode? node);
                    path.EncodeWith8Byte(compactPathKey);
                    ReadOnlySpan<byte> compactRlp = node!.FullRlp.AsSpan();
                    NodeRef compactNr = blobWriter.WriteRlp(compactRlp);
                    NodeRef.Write(nrBuf, in compactNr);
                    ref TWriter compactValueWriter = ref compactLevel.BeginValueWrite();
                    IByteBufferWriter.Copy(ref compactValueWriter, nrBuf);
                    compactLevel.FinishValueWrite(compactPathKey, NodeRef.Size);
                    bloom.Add(PersistedSnapshotBloomBuilder.StorageNodeKey(in addressHash, in path));
                }
                compactLevel.Build();
                perAddrHash.FinishValueWrite(PersistedSnapshotTags.StorageCompactSubTag);
            }

            // Sub-tag 0x00: Storage trie nodes (top, 4-byte path keys, length 0-5).
            int topStart = topIdx;
            while (topIdx < storTop.Count &&
                storTop[topIdx].AddrHash.Bytes[..PersistedSnapshotTags.AddressHashPrefixLength].SequenceEqual(addressHashPrefix))
                topIdx++;
            if (topStart < topIdx)
            {
                addrRefForStorageNode ??= new Hash256(in addressHash);
                ref TWriter topWriter = ref perAddrHash.BeginValueWrite();
                using HsstBTreeBuilderBuffers.Container topBuffers = new(expectedKeyCount: topIdx - topStart);
                using HsstBTreeBuilder<TWriter> topLevel = new(ref topWriter, ref topBuffers.Buffers, keyLength: 4,
                    expectedKeyCount: topIdx - topStart);
                for (int j = topStart; j < topIdx; j++)
                {
                    (ValueHash256 _, TreePath path) = storTop[j];
                    snapshot.TryGetStorageNode((addrRefForStorageNode, path), out TrieNode? node);
                    path.EncodeWith4Byte(topPathKey);
                    ReadOnlySpan<byte> topRlp = node!.FullRlp.AsSpan();
                    NodeRef topNr = blobWriter.WriteRlp(topRlp);
                    NodeRef.Write(nrBuf, in topNr);
                    ref TWriter topValueWriter = ref topLevel.BeginValueWrite();
                    IByteBufferWriter.Copy(ref topValueWriter, nrBuf);
                    topLevel.FinishValueWrite(topPathKey, NodeRef.Size);
                    bloom.Add(PersistedSnapshotBloomBuilder.StorageNodeKey(in addressHash, in path));
                }
                topLevel.Build();
                perAddrHash.FinishValueWrite(PersistedSnapshotTags.StorageTopSubTag);
            }

            perAddrHash.Build();
            addrLevel.FinishValueWrite(addressHashPrefix, perAddrHashWriter.Written - perAddrHashValueStart);
        }

        addrLevel.Build();
        outer.FinishValueWrite(PersistedSnapshotTags.StorageTrieColumnTag);
    }

    private static void WriteStateTopNodesColumn<TWriter>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot, NativeMemoryList<TreePath> stateNodeKeys, BlobArenaWriter blobWriter, BloomFilter bloom) where TWriter : IByteBufferWriter
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilderBuffers.Container innerBuffers = new(expectedKeyCount: stateNodeKeys.Count);
        using HsstBTreeBuilder<TWriter> inner = new(ref innerWriter, ref innerBuffers.Buffers, keyLength: 4, expectedKeyCount: stateNodeKeys.Count);
        Span<byte> keyBuffer = stackalloc byte[4];
        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];
        for (int i = 0; i < stateNodeKeys.Count; i++)
        {
            TreePath path = stateNodeKeys[i];
            snapshot.TryGetStateNode(path, out TrieNode? node);
            path.EncodeWith4Byte(keyBuffer);
            ReadOnlySpan<byte> rlp = node!.FullRlp.AsSpan();
            NodeRef nr = blobWriter.WriteRlp(rlp);
            NodeRef.Write(nrBuf, in nr);
            ref TWriter valueWriter = ref inner.BeginValueWrite();
            IByteBufferWriter.Copy(ref valueWriter, nrBuf);
            inner.FinishValueWrite(keyBuffer, NodeRef.Size);
            bloom.Add(PersistedSnapshotBloomBuilder.StatePathKey(in path));
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshotTags.StateTopNodesTag);
    }

    private static void WriteStateNodesColumnCompact<TWriter>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot, NativeMemoryList<TreePath> stateNodeKeys, BlobArenaWriter blobWriter, BloomFilter bloom) where TWriter : IByteBufferWriter
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilderBuffers.Container innerBuffers = new(expectedKeyCount: stateNodeKeys.Count);
        using HsstBTreeBuilder<TWriter> inner = new(ref innerWriter, ref innerBuffers.Buffers, keyLength: 8, expectedKeyCount: stateNodeKeys.Count);
        Span<byte> keyBuffer = stackalloc byte[8];
        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];
        for (int i = 0; i < stateNodeKeys.Count; i++)
        {
            TreePath path = stateNodeKeys[i];
            snapshot.TryGetStateNode(path, out TrieNode? node);
            path.EncodeWith8Byte(keyBuffer);
            ReadOnlySpan<byte> rlp = node!.FullRlp.AsSpan();
            NodeRef nr = blobWriter.WriteRlp(rlp);
            NodeRef.Write(nrBuf, in nr);
            ref TWriter valueWriter = ref inner.BeginValueWrite();
            IByteBufferWriter.Copy(ref valueWriter, nrBuf);
            inner.FinishValueWrite(keyBuffer, NodeRef.Size);
            bloom.Add(PersistedSnapshotBloomBuilder.StatePathKey(in path));
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshotTags.StateNodeTag);
    }

    private static void WriteStateNodesColumnFallback<TWriter>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot, NativeMemoryList<TreePath> stateNodeKeys, BlobArenaWriter blobWriter, BloomFilter bloom) where TWriter : IByteBufferWriter
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilderBuffers.Container innerBuffers = new(expectedKeyCount: stateNodeKeys.Count);
        using HsstBTreeBuilder<TWriter> inner = new(ref innerWriter, ref innerBuffers.Buffers, keyLength: 33, expectedKeyCount: stateNodeKeys.Count);
        Span<byte> keyBuffer = stackalloc byte[33];
        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];
        for (int i = 0; i < stateNodeKeys.Count; i++)
        {
            TreePath path = stateNodeKeys[i];
            snapshot.TryGetStateNode(path, out TrieNode? node);
            path.Path.Bytes.CopyTo(keyBuffer);
            keyBuffer[32] = (byte)path.Length;
            ReadOnlySpan<byte> rlp = node!.FullRlp.AsSpan();
            NodeRef nr = blobWriter.WriteRlp(rlp);
            NodeRef.Write(nrBuf, in nr);
            ref TWriter valueWriter = ref inner.BeginValueWrite();
            IByteBufferWriter.Copy(ref valueWriter, nrBuf);
            inner.FinishValueWrite(keyBuffer, NodeRef.Size);
            bloom.Add(PersistedSnapshotBloomBuilder.StatePathKey(in path));
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshotTags.StateNodeFallbackTag);
    }
}
