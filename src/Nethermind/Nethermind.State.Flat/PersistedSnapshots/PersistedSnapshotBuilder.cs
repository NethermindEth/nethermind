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
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Builds a single-level <see cref="SortedTable"/> from an in-memory <see cref="Snapshot"/>: every
/// entity becomes one fully-materialized <see cref="PersistedSnapshotKey"/> mapped to a small inline
/// value. Trie-node RLP values are stored as <see cref="NodeRef"/>s pointing into blob arenas;
/// account / slot / self-destruct / metadata values are inlined.
/// </summary>
/// <remarks>
/// The extraction + top/compact/fallback bucketing (and the comparers below) are kept unchanged from
/// the columnar builder so the entity ordering the future columnar builder/compacter rely on does not drift.
/// The materialized keys are streamed to a <see cref="SortedTableBuilder{TWriter}"/> in strictly
/// ascending key order — the builder enforces the order rather than sorting — so <see cref="Build"/>
/// emits by ascending column (ref-id, storage, state, per-address, metadata), merging the storage
/// sublists. The key encoding stores column / subcolumn tag bytes as <c>255 − tag</c> so that plain
/// ascending order reproduces the columnar reverse-tag emission order.
/// </remarks>
public static class PersistedSnapshotBuilder
{
    private const int TopPathThreshold = 7;
    private const int CompactPathThreshold = 15;

    private static readonly Comparison<TreePath> StateNodeComparer = (a, b) =>
    {
        int cmp = a.Path.Bytes.SequenceCompareTo(b.Path.Bytes);
        return cmp != 0 ? cmp : a.Length.CompareTo(b.Length);
    };

    // Sorts storage-trie node keys by 20-byte address-hash prefix (matching the column outer key)
    // and then by encoded path so per-addressHash slices are contiguous and emitted in sorted order.
    private static readonly Comparison<(ValueHash256 AddrHash, TreePath Path)> StorageNodeComparer = (a, b) =>
    {
        int cmp = a.AddrHash.Bytes[..PersistedSnapshotTags.AddressHashPrefixLength].SequenceCompareTo(b.AddrHash.Bytes[..PersistedSnapshotTags.AddressHashPrefixLength]);
        if (cmp != 0) return cmp;
        cmp = a.Path.Path.Bytes.SequenceCompareTo(b.Path.Path.Bytes);
        return cmp != 0 ? cmp : a.Path.Length.CompareTo(b.Path.Length);
    };

    // Sorts slot entries by raw Address bytes then by slot value, so per-address slices are
    // contiguous and slot keys within a slice are in sorted big-endian order.
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
        // at write time. PooledSet is used for the small Address dedup map so its
        // backing entry array is pool-rented rather than freshly allocated each block.
        NativeMemoryList<TreePath> stateTopKeys = null!, stateCompactKeys = null!, stateFallbackKeys = null!;
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storTopKeys = null!, storCompactKeys = null!, storFallbackKeys = null!;
        NativeMemoryList<((ValueAddress Addr, UInt256 Slot) Key, SlotValue? Value)> sortedStorages = null!;
        NativeMemoryList<ValueAddress> uniqueAddresses = null!;

        // Parallel extraction + sort: three independent jobs over disjoint dictionaries.
        Parallel.Invoke(
            () =>
            {
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

        SortedTableBuilder<TWriter> table = new(ref writer);
        try
        {
            // Records are streamed in strictly ascending key order (the builder enforces it), so emit
            // by ascending column: ref-id (0x00), storage nodes (0xFA), state fallback/compact/top
            // (0xFB/0xFC/0xFD), per-address accounts/self-destruct/slots (0xFE), metadata (0xFF).
            // Metadata is last so its blob_range records the now-final blob-arena run; the ref-id is
            // first but only needs the (fixed) blob-arena id.
            WriteRefId(ref table, blobWriter);
            WriteStorageNodes(ref table, snapshot, storFallbackKeys, storCompactKeys, storTopKeys, blobWriter, bloom);
            WriteStateNodes(ref table, snapshot, stateFallbackKeys, blobWriter, bloom);
            WriteStateNodes(ref table, snapshot, stateCompactKeys, blobWriter, bloom);
            WriteStateNodes(ref table, snapshot, stateTopKeys, blobWriter, bloom);
            WritePerAddress(ref table, snapshot, sortedStorages, uniqueAddresses, bloom);
            WriteMetadata(ref table, snapshot, blobWriter);

            table.Build();
        }
        finally
        {
            table.Dispose();
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
    /// Upper bound on the serialized snapshot size, used to pre-size the destination arena. The
    /// in-memory snapshot size bounds it comfortably: the metadata table stores only compact keys,
    /// small inline values, and 6-byte <see cref="NodeRef"/>s (the trie-node RLP it references lives in
    /// the blob arena), so the serialized table is far smaller than the in-memory snapshot it is built
    /// from. There is no artificial 2 GiB ceiling — the streaming
    /// <see cref="Sorted.SortedTableBuilder{TWriter}"/> builds tables past 2 GiB and the arena is
    /// long-addressed.
    /// </summary>
    public static long EstimateSize(Snapshot snapshot) => snapshot.EstimateMemory() + 1.KiB;

    private static void WritePerAddress<TWriter>(
        ref SortedTableBuilder<TWriter> table, Snapshot snapshot,
        NativeMemoryList<((ValueAddress Addr, UInt256 Slot) Key, SlotValue? Value)> sortedStorages,
        NativeMemoryList<ValueAddress> uniqueAddresses,
        BloomFilter bloom) where TWriter : IByteBufferWriter
    {
        // Slim-account RLP fits in 256 bytes; slot RLP (≤ RlpSlotValueBufferSize) reuses the same
        // buffer — table.Add copies each value out immediately, and slots are emitted before the
        // account for a given address, so there is no overlap.
        byte[] rlpBuffer = ArrayPool<byte>.Shared.Rent(256);
        RlpStream rlpStream = new(rlpBuffer);
        Span<byte> keyBuf = stackalloc byte[PersistedSnapshotKey.MaxKeyLength];
        Span<byte> slotKey = stackalloc byte[32];
        int storageIdx = 0;

        try
        {
            for (int addrIdx = 0; addrIdx < uniqueAddresses.Count; addrIdx++)
            {
                ValueAddress addrValue = uniqueAddresses[addrIdx];
                ReadOnlySpan<byte> addressBytes = addrValue.AsSpan;
                Address address = addrValue.ToAddress();

                ulong addrBloomKey = PersistedSnapshotBloomBuilder.AddressKey(addressBytes);
                bloom.Add(addrBloomKey);

                // Slots (sub-tag 0x02). Full 32-byte big-endian slot inline — no prefix/suffix split.
                while (storageIdx < sortedStorages.Count &&
                    sortedStorages[storageIdx].Key.Addr.AsSpan.SequenceEqual(addressBytes))
                {
                    SlotValue? value = sortedStorages[storageIdx].Value;
                    sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                    bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, slotKey));
                    // Present values are RLP-wrapped; null/deleted slots keep an empty payload so the
                    // length-0 = absent sentinel survives.
                    ReadOnlySpan<byte> payload = value.HasValue
                        ? rlpBuffer.AsSpan(0, Rlp.Encode(value.Value.AsReadOnlySpan.WithoutLeadingZeros(), rlpBuffer))
                        : [];
                    int len = PersistedSnapshotKey.WriteSlotKey(keyBuf, addressBytes, slotKey);
                    table.Add(keyBuf[..len], payload);
                    storageIdx++;
                }

                // Self-destruct (sub-tag 0x01).
                if (snapshot.Content.SelfDestructedStorageAddresses.TryGetValue(address, out bool sdValue))
                {
                    int len = PersistedSnapshotKey.WriteSelfDestructKey(keyBuf, addressBytes);
                    table.Add(keyBuf[..len],
                        sdValue ? PersistedSnapshotTags.SelfDestructNewMarker : PersistedSnapshotTags.SelfDestructDestructedMarker);
                }

                // Account (sub-tag 0x00). Slim RLP starts with a list header (0xc0+), so the
                // [0x00] deleted-marker is unambiguous against any valid RLP.
                if (snapshot.TryGetAccount(address, out Account? account))
                {
                    int len = PersistedSnapshotKey.WriteAccountKey(keyBuf, addressBytes);
                    if (account is null)
                    {
                        table.Add(keyBuf[..len], PersistedSnapshotTags.AccountDeletedMarker);
                    }
                    else
                    {
                        int rlpLen = AccountDecoder.Slim.GetLength(account);
                        rlpStream.Reset();
                        AccountDecoder.Slim.Encode(rlpStream, account);
                        table.Add(keyBuf[..len], rlpBuffer.AsSpan(0, rlpLen));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rlpBuffer);
        }
    }

    private static void WriteStateNodes<TWriter>(
        ref SortedTableBuilder<TWriter> table, Snapshot snapshot,
        NativeMemoryList<TreePath> keys, BlobArenaWriter blobWriter, BloomFilter bloom) where TWriter : IByteBufferWriter
    {
        Span<byte> keyBuf = stackalloc byte[PersistedSnapshotKey.MaxKeyLength];
        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];
        for (int i = 0; i < keys.Count; i++)
        {
            TreePath path = keys[i];
            if (!snapshot.TryGetStateNode(path, out TrieNode? node) || node is null)
                throw new InvalidOperationException($"State node {path} disappeared between extraction and persist.");
            NodeRef nr = blobWriter.WriteRlp(node.FullRlp.AsSpan());
            NodeRef.Write(nrBuf, in nr);
            int len = PersistedSnapshotKey.WriteStateNodeKey(keyBuf, in path);
            table.Add(keyBuf[..len], nrBuf);
            bloom.Add(PersistedSnapshotBloomBuilder.StatePathKey(in path));
        }
    }

    /// <summary>
    /// Emit storage-trie nodes (column 0xFA) in ascending key order via a 3-way merge of the
    /// fallback / compact / top sublists. The sub-column byte (fallback 0xFD &lt; compact 0xFE &lt; top
    /// 0xFF) follows the 20-byte address-hash, so for each address-hash all fallback nodes precede
    /// compact, which precede top; each sublist is already sorted by address-hash → path and the path
    /// encodings preserve that order, so the merged stream is strictly ascending.
    /// </summary>
    private static void WriteStorageNodes<TWriter>(
        ref SortedTableBuilder<TWriter> table, Snapshot snapshot,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> fallback,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> compact,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> top,
        BlobArenaWriter blobWriter, BloomFilter bloom) where TWriter : IByteBufferWriter
    {
        Span<byte> keyBuf = stackalloc byte[PersistedSnapshotKey.MaxKeyLength];
        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];
        // Cache the materialised Hash256 across a per-addressHash run — the merge keeps all of an
        // address-hash's nodes (across sublists) contiguous, so one Gen0 alloc per address-hash.
        ValueHash256 cachedHash = default;
        Hash256? cachedRef = null;
        int fi = 0, ci = 0, ti = 0;
        while (true)
        {
            bool hasF = fi < fallback.Count, hasC = ci < compact.Count, hasT = ti < top.Count;
            if (!hasF && !hasC && !hasT) break;

            // Smallest head by (addressHash, sub-rank fallback<compact<top). Strict-less keeps the
            // lower sub-rank on an address-hash tie (fallback first, then compact, then top).
            int pick;
            if (hasF && (!hasC || !AddrHashLess(compact[ci].AddrHash, fallback[fi].AddrHash))
                     && (!hasT || !AddrHashLess(top[ti].AddrHash, fallback[fi].AddrHash)))
                pick = 0;
            else if (hasC && (!hasT || !AddrHashLess(top[ti].AddrHash, compact[ci].AddrHash)))
                pick = 1;
            else
                pick = 2;

            (ValueHash256 addressHash, TreePath path) = pick == 0 ? fallback[fi++] : pick == 1 ? compact[ci++] : top[ti++];
            if (cachedRef is null || !cachedHash.Equals(addressHash))
            {
                cachedHash = addressHash;
                cachedRef = new Hash256(in addressHash);
            }
            if (!snapshot.TryGetStorageNode((cachedRef, path), out TrieNode? node) || node is null)
                throw new InvalidOperationException($"Storage node {addressHash}:{path} disappeared between extraction and persist.");
            NodeRef nr = blobWriter.WriteRlp(node.FullRlp.AsSpan());
            NodeRef.Write(nrBuf, in nr);
            int len = PersistedSnapshotKey.WriteStorageNodeKey(keyBuf, addressHash.Bytes, in path);
            table.Add(keyBuf[..len], nrBuf);
            bloom.Add(PersistedSnapshotBloomBuilder.StorageNodeKey(in addressHash, in path));
        }
    }

    private static bool AddrHashLess(in ValueHash256 a, in ValueHash256 b) =>
        a.Bytes[..PersistedSnapshotKey.AddressHashPrefixLength]
            .SequenceCompareTo(b.Bytes[..PersistedSnapshotKey.AddressHashPrefixLength]) < 0;

    /// <summary>Emit the single referenced blob-arena id record (column 0x00, sorts first). A base
    /// snapshot writes all its trie RLP through one blob arena, so there is exactly one.</summary>
    private static void WriteRefId<TWriter>(ref SortedTableBuilder<TWriter> table, BlobArenaWriter blobWriter)
        where TWriter : IByteBufferWriter
    {
        Span<byte> refIdKey = stackalloc byte[PersistedSnapshotKey.RefIdKeyLength];
        int refIdLen = PersistedSnapshotKey.WriteRefIdKey(refIdKey, blobWriter.BlobArenaId);
        table.Add(refIdKey[..refIdLen], PersistedSnapshotTags.RefIdValue);
    }

    private static void WriteMetadata<TWriter>(
        ref SortedTableBuilder<TWriter> table, Snapshot snapshot, BlobArenaWriter blobWriter) where TWriter : IByteBufferWriter
    {
        // blob_range is this base snapshot's contiguous trie-RLP run in the single blob arena it
        // targeted — every trie node above wrote through this same blobWriter, so the run is final.
        BlobRange blobRange = blobWriter.Written > blobWriter.StartOffset
            ? new BlobRange(blobWriter.BlobArenaId, blobWriter.StartOffset, blobWriter.Written - blobWriter.StartOffset)
            : BlobRange.None;

        Span<byte> keyBuf = stackalloc byte[1 + PersistedSnapshotTags.MetadataKeyLength];
        Span<byte> blockNumBytes = stackalloc byte[8];
        Span<byte> blobRangeBytes = stackalloc byte[BlobRange.SerializedSize];

        blobRange.Write(blobRangeBytes);
        AddMetadata(ref table, keyBuf, PersistedSnapshotTags.MetadataBlobRangeKey, blobRangeBytes);

        BitConverter.TryWriteBytes(blockNumBytes, snapshot.From.BlockNumber);
        AddMetadata(ref table, keyBuf, PersistedSnapshotTags.MetadataFromBlockKey, blockNumBytes);
        AddMetadata(ref table, keyBuf, PersistedSnapshotTags.MetadataFromHashKey, snapshot.From.StateRoot.Bytes);

        // The ref-id record (column 0x00) sorts before everything and is emitted up front by WriteRefId.
        BitConverter.TryWriteBytes(blockNumBytes, snapshot.To.BlockNumber);
        AddMetadata(ref table, keyBuf, PersistedSnapshotTags.MetadataToBlockKey, blockNumBytes);
        AddMetadata(ref table, keyBuf, PersistedSnapshotTags.MetadataToHashKey, snapshot.To.StateRoot.Bytes);

        AddMetadata(ref table, keyBuf, PersistedSnapshotTags.MetadataVersionKey, PersistedSnapshotTags.MetadataFormatVersion);
    }

    private static void AddMetadata<TWriter>(ref SortedTableBuilder<TWriter> table, scoped Span<byte> keyBuf,
        scoped ReadOnlySpan<byte> name, scoped ReadOnlySpan<byte> value) where TWriter : IByteBufferWriter
    {
        int len = PersistedSnapshotKey.WriteMetadataKey(keyBuf, name);
        table.Add(keyBuf[..len], value);
    }
}
