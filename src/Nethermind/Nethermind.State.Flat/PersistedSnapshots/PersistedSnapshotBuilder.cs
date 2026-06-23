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
/// The extraction + sort + top/compact/fallback bucketing (and the comparers below) are kept
/// unchanged from the HSST builder so the entity ordering the future HSST builder/compacter rely on
/// does not drift. Only the serialization changed: instead of nested HSST columns, the materialized
/// keys are fed to a <see cref="SortedTableBuilder{TWriter}"/>, which sorts them ascending at
/// <c>Build</c>. The key encoding stores column / subcolumn tag bytes as <c>255 − tag</c> so that
/// plain ascending order reproduces the HSST reverse-tag emission order.
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

        int expectedKeys = snapshot.StateNodesCount + snapshot.StorageNodesCount
            + uniqueAddresses.Count + sortedStorages.Count + 8;
        SortedTableBuilder<TWriter> table = new(ref writer, expectedKeys);
        try
        {
            // Emission order is free — the table sorts all keys at Build. Per-address (accounts /
            // self-destruct / slots) and trie nodes come first; metadata is written last so its
            // blob_range entry can record the now-final blob-arena run this snapshot wrote.
            WritePerAddress(ref table, snapshot, sortedStorages, uniqueAddresses, bloom);
            WriteStateNodes(ref table, snapshot, stateTopKeys, blobWriter, bloom);
            WriteStateNodes(ref table, snapshot, stateCompactKeys, blobWriter, bloom);
            WriteStateNodes(ref table, snapshot, stateFallbackKeys, blobWriter, bloom);
            WriteStorageNodes(ref table, snapshot, storTopKeys, blobWriter, bloom);
            WriteStorageNodes(ref table, snapshot, storCompactKeys, blobWriter, bloom);
            WriteStorageNodes(ref table, snapshot, storFallbackKeys, blobWriter, bloom);
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
    /// Estimate of the serialized snapshot size, used to size the destination arena
    /// reservation. Capped at 2 GiB — the hard ceiling on a Full snapshot — which also
    /// keeps the value within <see cref="int"/>.MaxValue for contiguous-buffer callers.
    /// </summary>
    public static long EstimateSize(Snapshot snapshot) =>
        Math.Min(2.GiB, snapshot.EstimateMemory() + 1.KiB);

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

        ArrayPool<byte>.Shared.Return(rlpBuffer);
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
            snapshot.TryGetStateNode(path, out TrieNode? node);
            NodeRef nr = blobWriter.WriteRlp(node!.FullRlp.AsSpan());
            NodeRef.Write(nrBuf, in nr);
            int len = PersistedSnapshotKey.WriteStateNodeKey(keyBuf, in path);
            table.Add(keyBuf[..len], nrBuf);
            bloom.Add(PersistedSnapshotBloomBuilder.StatePathKey(in path));
        }
    }

    private static void WriteStorageNodes<TWriter>(
        ref SortedTableBuilder<TWriter> table, Snapshot snapshot,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> keys, BlobArenaWriter blobWriter, BloomFilter bloom) where TWriter : IByteBufferWriter
    {
        Span<byte> keyBuf = stackalloc byte[PersistedSnapshotKey.MaxKeyLength];
        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];
        // Lists are sorted by addressHash prefix → path, so cache the materialised Hash256 across
        // a per-addressHash run (one Gen0 alloc per addressHash instead of per node).
        ValueHash256 cachedHash = default;
        Hash256? cachedRef = null;
        for (int i = 0; i < keys.Count; i++)
        {
            (ValueHash256 addressHash, TreePath path) = keys[i];
            if (cachedRef is null || !cachedHash.Equals(addressHash))
            {
                cachedHash = addressHash;
                cachedRef = new Hash256(in addressHash);
            }
            snapshot.TryGetStorageNode((cachedRef, path), out TrieNode? node);
            NodeRef nr = blobWriter.WriteRlp(node!.FullRlp.AsSpan());
            NodeRef.Write(nrBuf, in nr);
            int len = PersistedSnapshotKey.WriteStorageNodeKey(keyBuf, addressHash.Bytes, in path);
            table.Add(keyBuf[..len], nrBuf);
            bloom.Add(PersistedSnapshotBloomBuilder.StorageNodeKey(in addressHash, in path));
        }
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

        // A base snapshot writes all its trie RLP through one blob arena — one referenced id.
        Span<byte> refIdKey = stackalloc byte[PersistedSnapshotKey.RefIdKeyLength];
        int refIdLen = PersistedSnapshotKey.WriteRefIdKey(refIdKey, blobWriter.BlobArenaId);
        table.Add(refIdKey[..refIdLen], PersistedSnapshotTags.RefIdValue);

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
