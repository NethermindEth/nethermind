// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using HsstEnumerator = Nethermind.State.Flat.Hsst.HsstEnumerator<Nethermind.State.Flat.Storage.WholeReadSessionReader, Nethermind.State.Flat.Hsst.NoOpPin>;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Builds columnar HSST byte data from an in-memory <see cref="Snapshot"/>.
/// The outer HSST has 7 column entries, each containing an inner HSST.
/// Inner HSST keys are the entity keys without the tag prefix.
///
/// Snapshot types:
/// - Full: all values written directly. Trie RLP values are non-inline (large).
///   Slot suffix values are inline (small).
/// - Linked: only trie columns (0x03, 0x05, 0x06, 0x07 inner, 0x08 inner) become
///   NodeRef(8 bytes, inline) pointing to the Full snapshot's data region.
///   Account (0x01), slot, and self-destruct values are copied as-is (not NodeRefs).
///
/// Size cap: a Full persisted snapshot cannot exceed 2 GiB.
/// <see cref="NodeRef.RlpDataOffset"/> is a 32-bit int that addresses bytes inside
/// the referenced Full snapshot, so any byte past 2 GiB is unreachable from a Linked
/// snapshot's NodeRef. <see cref="ConvertFullToLinked"/> enforces this with an
/// upfront snapshot-size precondition that throws with snapshot identity if violated.
/// In practice a Full snapshot covers at most <c>compactSize</c> blocks (the granularity
/// at which PersistenceManager produces base snapshots) — on mainnet that is around
/// 40 MiB, so the 2 GiB ceiling is far above the working range.
/// </summary>
public static class PersistedSnapshotBuilder
{
    private const int TopPathThreshold = 5;
    private const int CompactPathThreshold = 15;
    private const int StorageHashPrefixLength = 20;

    // Outer HSST column tags in iteration order. Shared between ConvertFullToLinked and
    // NWayMergeSnapshots. Storage-trie data lives inside the per-address column 0x01 as
    // sub-tags, so 0x07/0x08 are gone from the on-disk layout.
    private static readonly byte[][] s_columnTags =
    [
        PersistedSnapshot.MetadataTag,
        PersistedSnapshot.AccountColumnTag,
        PersistedSnapshot.StateNodeTag,
        PersistedSnapshot.StateTopNodesTag,
        PersistedSnapshot.StateNodeFallbackTag,
    ];

    private static readonly Comparison<TreePath> StateNodeComparer = (a, b) =>
    {
        int cmp = a.Path.Bytes.SequenceCompareTo(b.Path.Bytes);
        return cmp != 0 ? cmp : a.Length.CompareTo(b.Length);
    };

    // Sorts storage-trie node keys by 20-byte address-hash prefix (matching the column-0x01
    // outer key) and then by encoded path so per-address slices are contiguous and the
    // inner HSST keys are in sorted order.
    private static readonly Comparison<(ValueHash256 AddrHash, TreePath Path)> StorageNodeComparer = (a, b) =>
    {
        int cmp = a.AddrHash.Bytes[..StorageHashPrefixLength].SequenceCompareTo(b.AddrHash.Bytes[..StorageHashPrefixLength]);
        if (cmp != 0) return cmp;
        cmp = a.Path.Path.Bytes.SequenceCompareTo(b.Path.Path.Bytes);
        return cmp != 0 ? cmp : a.Path.Length.CompareTo(b.Path.Length);
    };

    private static readonly Comparison<((ValueHash256 AddrHash, UInt256 Slot) Key, SlotValue? Value)> StoragesByAddrHashComparer = (a, b) =>
    {
        int cmp = a.Key.AddrHash.Bytes[..StorageHashPrefixLength].SequenceCompareTo(b.Key.AddrHash.Bytes[..StorageHashPrefixLength]);
        if (cmp != 0) return cmp;
        return a.Key.Slot.CompareTo(b.Key.Slot);
    };

    public static void Build<TWriter, TReader, TPin>(Snapshot snapshot, ref TWriter writer, BlobArenaWriter blobWriter, BloomFilter? bloom = null, BloomFilter? trieBloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // To stay off the LOH, we keep only the unmanaged sort keys in NativeMemoryList
        // (off-heap) and re-fetch the TrieNode value from the source ConcurrentDictionary
        // at column-write time. PooledDictionary is used for the small Address ↔ hash maps
        // so their backing entry arrays are pool-rented rather than freshly allocated each
        // block.
        NativeMemoryList<TreePath> stateTopKeys = null!, stateCompactKeys = null!, stateFallbackKeys = null!;
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storTopKeys = null!, storCompactKeys = null!, storFallbackKeys = null!;
        // Storages carry the address hash inline so the sort comparator does not need any
        // dict lookup, and column-write iteration can match by hash directly.
        NativeMemoryList<((ValueHash256 AddrHash, UInt256 Slot) Key, SlotValue? Value)> sortedStorages = null!;
        // Per-address column 0x01 needs a sorted list of unique address-hashes plus a way
        // to recover the Address bytes for account / SD lookups. uniqueAddressHashes is
        // sorted by full ValueHash256 (a strict refinement of the 20-byte prefix sort the
        // column key requires). hashToAddr is also sorted by hash and contains a (hash,
        // 20-byte address) entry for every hash that originated from accounts / SD / slots
        // (i.e. every hash with a known Address); storage-trie-only hashes are absent. We
        // walk uniqueAddressHashes and hashToAddr in lock-step at write time.
        NativeMemoryList<ValueHash256> uniqueAddressHashes = null!;
        NativeMemoryList<(ValueHash256 Hash, ValueAddress Addr)> hashToAddr = null!;

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
                // Job B: storage trie nodes — store (ValueHash256, TreePath) keys off-heap.
                // Column writers materialize a fresh Hash256 from the value hash on demand
                // (one Gen0 alloc per address that has storage-trie nodes) for the
                // snapshot.TryGetStorageNode lookup.
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
                // Job C: account column prep — collect Address-keyed sources (accounts /
                // SD / slots), pre-hash each address once into uniqueAddressHashes, and
                // build hashToAddr. Storages carry the address hash inline so we do not
                // need a separate addrToHash dict for the sort comparator.
                using PooledSet<HashedKey<Address>> seen = new();
                foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts)
                    seen.Add(kv.Key);
                foreach (KeyValuePair<HashedKey<Address>, bool> kv in snapshot.SelfDestructedStorageAddresses)
                    seen.Add(kv.Key);

                NativeMemoryList<((ValueHash256 AddrHash, UInt256 Slot) Key, SlotValue? Value)> storages =
                    new(Math.Max(1, snapshot.StoragesCount));
                foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> kv in snapshot.Storages)
                {
                    (Address addr, UInt256 slot) = kv.Key.Key;
                    ValueHash256 addrHash = ValueKeccak.Compute(addr.Bytes);
                    storages.Add(((addrHash, slot), kv.Value));
                    seen.Add(addr);
                }

                NativeMemoryList<ValueHash256> hashes = new(Math.Max(1, seen.Count));
                NativeMemoryList<(ValueHash256 Hash, ValueAddress Addr)> addrMap = new(Math.Max(1, seen.Count));
                foreach (HashedKey<Address> addr in seen)
                {
                    ValueHash256 vh = ValueKeccak.Compute(addr.Key.Bytes);
                    hashes.Add(vh);
                    addrMap.Add((vh, new ValueAddress(addr.Key.Bytes)));
                }
                addrMap.Sort(static (a, b) => a.Hash.CompareTo(b.Hash));

                storages.Sort(StoragesByAddrHashComparer);

                sortedStorages = storages;
                uniqueAddressHashes = hashes;
                hashToAddr = addrMap;
            });

        // After Parallel.Invoke: merge in storage-trie-only address-hashes (those that
        // appear in StorageNodes but not in Accounts/SD/Slots, so Job C didn't see them).
        // We append everything to uniqueAddressHashes, sort, and dedupe in place.
        // Sorting by full ValueHash256 is a strict refinement of the 20-byte prefix order
        // that column 0x01 outer keys require, so downstream emit order is preserved.
        {
            int extraCapacity = storTopKeys.Count + storCompactKeys.Count + storFallbackKeys.Count;
            uniqueAddressHashes.EnsureCapacity(uniqueAddressHashes.Count + extraCapacity);
            for (int i = 0; i < storTopKeys.Count; i++) uniqueAddressHashes.Add(storTopKeys[i].AddrHash);
            for (int i = 0; i < storCompactKeys.Count; i++) uniqueAddressHashes.Add(storCompactKeys[i].AddrHash);
            for (int i = 0; i < storFallbackKeys.Count; i++) uniqueAddressHashes.Add(storFallbackKeys[i].AddrHash);
            uniqueAddressHashes.Sort((a, b) => a.CompareTo(b));

            // Linear in-place dedupe: keep first of each consecutive run.
            Span<ValueHash256> span = uniqueAddressHashes.AsSpan();
            int write = 0;
            for (int read = 0; read < span.Length; read++)
            {
                if (write == 0 || !span[read].Equals(span[write - 1]))
                {
                    span[write++] = span[read];
                }
            }
            uniqueAddressHashes.Truncate(write);
        }

        HsstDenseByteIndexBuilder<TWriter> outer = new(ref writer);
        try
        {
            // Column 0x00: Metadata
            WriteMetadataColumn<TWriter, TReader, TPin>(ref outer, snapshot, blobWriter.BlobArenaId);

            // Column 0x01: Unified per-address column. Sub-tags 0x01 (storage trie top),
            // 0x02 (storage trie compact), 0x03 (storage trie fallback), 0x04 (slots),
            // 0x05 (account RLP), 0x06 (SD).
            WriteAccountColumn<TWriter, TReader, TPin>(ref outer, snapshot, sortedStorages, uniqueAddressHashes,
                hashToAddr,
                storTopKeys, storCompactKeys, storFallbackKeys, blobWriter, bloom, trieBloom);

            // Column 0x03: State nodes (compact, path length 6-15)
            WriteStateNodesColumnCompact<TWriter, TReader, TPin>(ref outer, snapshot, stateCompactKeys, blobWriter, trieBloom);

            // Column 0x05: State top nodes (path length 0-5)
            WriteStateTopNodesColumn<TWriter, TReader, TPin>(ref outer, snapshot, stateTopKeys, blobWriter, trieBloom);

            // Column 0x06: State nodes fallback (path length 16+)
            WriteStateNodesColumnFallback<TWriter, TReader, TPin>(ref outer, snapshot, stateFallbackKeys, blobWriter, trieBloom);

            outer.Build();
        }
        finally
        {
            outer.Dispose();
            sortedStorages?.Dispose();
            uniqueAddressHashes?.Dispose();
            hashToAddr?.Dispose();
            stateTopKeys?.Dispose();
            stateCompactKeys?.Dispose();
            stateFallbackKeys?.Dispose();
            storTopKeys?.Dispose();
            storCompactKeys?.Dispose();
            storFallbackKeys?.Dispose();
        }
    }

    /// <summary>
    /// Estimate of the serialized Full snapshot size, used to size the destination arena
    /// reservation. Capped at 2 GiB — the hard ceiling on a Full snapshot (see the
    /// <see cref="NodeRef.RlpDataOffset"/> note on the class doc above). Returned as
    /// <see cref="long"/> so callers feeding this into long-typed APIs (e.g. arena
    /// reservations) don't truncate; the cap also keeps the value within
    /// <see cref="int"/>.MaxValue for callers that need to allocate a contiguous buffer.
    /// </summary>
    public static long EstimateSize(Snapshot snapshot) =>
        Math.Min(2.GiB, snapshot.EstimateMemory() + 1.KiB);

    private static void WriteMetadataColumn<TWriter, TReader, TPin>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot, int blobArenaId) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // Metadata keys must be in sorted ASCII order:
        // "from_block" < "from_hash" < "ref_ids" < "to_block" < "to_hash" < "version"
        // ref_ids carries this snapshot's referenced blob arena id(s). For a freshly built
        // base snapshot it's a single int — the id of the blob arena the builder just wrote
        // its trie RLPs into. Compactor's NWayMetadataMerge replaces this with the union
        // of input snapshots' referenced ids.
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilder<TWriter, TReader, TPin> inner = new(ref innerWriter, expectedKeyCount: 6);

        Span<byte> blockNumBytes = stackalloc byte[8];
        Span<byte> refIdsBytes = stackalloc byte[4];

        BitConverter.TryWriteBytes(blockNumBytes, snapshot.From.BlockNumber);
        inner.Add("from_block"u8, blockNumBytes);

        inner.Add("from_hash"u8, snapshot.From.StateRoot.Bytes);

        BitConverter.TryWriteBytes(refIdsBytes, blobArenaId);
        inner.Add("ref_ids"u8, refIdsBytes);

        BitConverter.TryWriteBytes(blockNumBytes, snapshot.To.BlockNumber);
        inner.Add("to_block"u8, blockNumBytes);

        inner.Add("to_hash"u8, snapshot.To.StateRoot.Bytes);

        inner.Add("version"u8, [0x01]);

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshot.MetadataTag);
    }

    private static void WriteAccountColumn<TWriter, TReader, TPin>(
        ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot,
        NativeMemoryList<((ValueHash256 AddrHash, UInt256 Slot) Key, SlotValue? Value)> sortedStorages,
        NativeMemoryList<ValueHash256> uniqueAddressHashes,
        NativeMemoryList<(ValueHash256 Hash, ValueAddress Addr)> hashToAddr,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storTop,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storCompact,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storFallback,
        BlobArenaWriter blobWriter,
        BloomFilter? bloom = null,
        BloomFilter? trieBloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        const int slotPrefixLength = 31;

        // Address-level HSST keyed by 20-byte address-hash prefix.
        ref TWriter addressWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilder<TWriter, TReader, TPin> addressLevel = new(ref addressWriter, new HsstBTreeOptions
        {
            MinSeparatorLength = 4,
        }, expectedKeyCount: uniqueAddressHashes.Count);
        byte[] rlpBuffer = new byte[256];
        RlpStream rlpStream = new(rlpBuffer);
        Span<byte> slotKey = stackalloc byte[32];
        Span<byte> currentPrefixBuf = stackalloc byte[slotPrefixLength];
        Span<byte> topPathKey = stackalloc byte[3];
        Span<byte> compactPathKey = stackalloc byte[8];
        Span<byte> fallbackPathKey = stackalloc byte[33];
        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];
        int storageIdx = 0;
        int storTopIdx = 0;
        int storCompactIdx = 0;
        int storFallbackIdx = 0;
        // hashToAddr is sorted by hash and is a subset of uniqueAddressHashes (also sorted
        // by hash), so we can resolve hash → Address with a forward-only walk instead of
        // a per-iteration lookup. hashToAddrIdx is left pointing at the next unconsumed
        // entry; when it matches the current addressHash we materialize an Address ref
        // (single Gen0 alloc per outer iteration that has account-side data).
        int hashToAddrIdx = 0;

        for (int addrIdx = 0; addrIdx < uniqueAddressHashes.Count; addrIdx++)
        {
            ValueHash256 addressHash = uniqueAddressHashes[addrIdx];
            // address is null when this column key was contributed only by storage-trie
            // nodes (Hash256 → TrieNode). In that case slots/account/SD lookups are
            // skipped because all three are keyed by raw Address.
            Address? address = null;
            if (hashToAddrIdx < hashToAddr.Count && hashToAddr[hashToAddrIdx].Hash.Equals(addressHash))
            {
                address = hashToAddr[hashToAddrIdx].Addr.ToAddress();
                hashToAddrIdx++;
            }
            ReadOnlySpan<byte> addressHashPrefix = addressHash.Bytes[..StorageHashPrefixLength];

            ulong addrBloomKey = 0;
            if (bloom is not null)
            {
                addrBloomKey = PersistedSnapshotBloomBuilder.AddressKey(in addressHash);
                bloom.Add(addrBloomKey);
            }

            // Begin per-address HSST. Up to 6 sub-tags 0x01..0x06; DenseByteIndex addresses
            // entries by tag-byte directly and gap-fills missing positions with length-0
            // values. Sub-tag value-presence semantics:
            //   0x01 storage top: nested HSST(3-byte path → RLP)
            //   0x02 storage compact: nested HSST(8-byte path → RLP)
            //   0x03 storage fallback: nested HSST(33-byte path → RLP)
            //   0x04 slots: nested HSST(SlotPrefix(31) → ByteTagMap)
            //   0x05 account: [] absent / [0x00] deleted / RLP-bytes present
            //   0x06 SD: [] absent / [0x00] destructed / [0x01] new account
            ref TWriter perAddrWriter = ref addressLevel.BeginValueWrite();
            using HsstDenseByteIndexBuilder<TWriter> perAddr = new(ref perAddrWriter);

            // Hash256 needed only when there are storage-trie nodes for this address; the
            // map has an entry iff at least one storTop/storCompact/storFallback key
            // referenced it during Job B.
            Hash256? addrRefForStorageNode = null;

            // Sub-tag 0x01: Storage trie nodes (top, 3-byte path keys, length 0-5).
            // Storage-trie partitions are pre-sorted by address-hash prefix and path so a
            // single advance through storTop / storCompact / storFallback covers the run
            // for this address-hash.
            int topStart = storTopIdx;
            while (storTopIdx < storTop.Count &&
                storTop[storTopIdx].AddrHash.Bytes[..StorageHashPrefixLength].SequenceEqual(addressHashPrefix))
                storTopIdx++;
            if (topStart < storTopIdx)
            {
                addrRefForStorageNode ??= new Hash256(in addressHash);
                ref TWriter topWriter = ref perAddr.BeginValueWrite();
                using HsstBTreeBuilder<TWriter, TReader, TPin> topLevel = new(ref topWriter, new HsstBTreeOptions { MinSeparatorLength = 3 },
                    expectedKeyCount: storTopIdx - topStart);
                for (int i = topStart; i < storTopIdx; i++)
                {
                    (ValueHash256 _, TreePath path) = storTop[i];
                    snapshot.TryGetStorageNode((addrRefForStorageNode, path), out TrieNode? node);
                    path.EncodeWith3Byte(topPathKey);
                    ReadOnlySpan<byte> topRlp = node!.FullRlp.AsSpan();
                    NodeRef topNr = blobWriter.WriteRlp(topRlp);
                    NodeRef.Write(nrBuf, in topNr);
                    ref TWriter topValueWriter = ref topLevel.BeginValueWrite();
                    IByteBufferWriter.Copy(ref topValueWriter, nrBuf);
                    topLevel.FinishValueWrite(topPathKey, NodeRef.Size);
                    trieBloom?.Add(PersistedSnapshotBloomBuilder.StorageNodeKey(in addressHash, in path));
                }
                topLevel.Build();
                perAddr.FinishValueWrite(PersistedSnapshot.StorageTopSubTag);
            }

            // Sub-tag 0x02: Storage trie nodes (compact, 8-byte path keys, length 6-15).
            int compactStart = storCompactIdx;
            while (storCompactIdx < storCompact.Count &&
                storCompact[storCompactIdx].AddrHash.Bytes[..StorageHashPrefixLength].SequenceEqual(addressHashPrefix))
                storCompactIdx++;
            if (compactStart < storCompactIdx)
            {
                addrRefForStorageNode ??= new Hash256(in addressHash);
                ref TWriter compactWriter = ref perAddr.BeginValueWrite();
                using HsstBTreeBuilder<TWriter, TReader, TPin> compactLevel = new(ref compactWriter, new HsstBTreeOptions { MinSeparatorLength = 8 },
                    expectedKeyCount: storCompactIdx - compactStart);
                for (int i = compactStart; i < storCompactIdx; i++)
                {
                    (ValueHash256 _, TreePath path) = storCompact[i];
                    snapshot.TryGetStorageNode((addrRefForStorageNode, path), out TrieNode? node);
                    path.EncodeWith8Byte(compactPathKey);
                    ReadOnlySpan<byte> compactRlp = node!.FullRlp.AsSpan();
                    NodeRef compactNr = blobWriter.WriteRlp(compactRlp);
                    NodeRef.Write(nrBuf, in compactNr);
                    ref TWriter compactValueWriter = ref compactLevel.BeginValueWrite();
                    IByteBufferWriter.Copy(ref compactValueWriter, nrBuf);
                    compactLevel.FinishValueWrite(compactPathKey, NodeRef.Size);
                    trieBloom?.Add(PersistedSnapshotBloomBuilder.StorageNodeKey(in addressHash, in path));
                }
                compactLevel.Build();
                perAddr.FinishValueWrite(PersistedSnapshot.StorageCompactSubTag);
            }

            // Sub-tag 0x03: Storage trie nodes (fallback, 33-byte path keys, length 16+).
            int fallbackStart = storFallbackIdx;
            while (storFallbackIdx < storFallback.Count &&
                storFallback[storFallbackIdx].AddrHash.Bytes[..StorageHashPrefixLength].SequenceEqual(addressHashPrefix))
                storFallbackIdx++;
            if (fallbackStart < storFallbackIdx)
            {
                addrRefForStorageNode ??= new Hash256(in addressHash);
                ref TWriter fbWriter = ref perAddr.BeginValueWrite();
                using HsstBTreeBuilder<TWriter, TReader, TPin> fbLevel = new(ref fbWriter, expectedKeyCount: storFallbackIdx - fallbackStart);
                for (int i = fallbackStart; i < storFallbackIdx; i++)
                {
                    (ValueHash256 _, TreePath path) = storFallback[i];
                    snapshot.TryGetStorageNode((addrRefForStorageNode, path), out TrieNode? node);
                    path.Path.Bytes.CopyTo(fallbackPathKey);
                    fallbackPathKey[32] = (byte)path.Length;
                    ReadOnlySpan<byte> fbRlp = node!.FullRlp.AsSpan();
                    NodeRef fbNr = blobWriter.WriteRlp(fbRlp);
                    NodeRef.Write(nrBuf, in fbNr);
                    ref TWriter fbValueWriter = ref fbLevel.BeginValueWrite();
                    IByteBufferWriter.Copy(ref fbValueWriter, nrBuf);
                    fbLevel.FinishValueWrite(fallbackPathKey, NodeRef.Size);
                    trieBloom?.Add(PersistedSnapshotBloomBuilder.StorageNodeKey(in addressHash, in path));
                }
                fbLevel.Build();
                perAddr.FinishValueWrite(PersistedSnapshot.StorageFallbackSubTag);
            }

            // Sub-tag 0x04: Slots — skipped when no Address is known for this hash key.
            bool hasStorage = address is not null && storageIdx < sortedStorages.Count &&
                sortedStorages[storageIdx].Key.AddrHash.Equals(addressHash);
            if (hasStorage)
            {
                ref TWriter slotWriter = ref perAddr.BeginValueWrite();
                using HsstBTreeBuilder<TWriter, TReader, TPin> prefixLevel = new(ref slotWriter, new HsstBTreeOptions { MinSeparatorLength = 4 });

                while (storageIdx < sortedStorages.Count &&
                    sortedStorages[storageIdx].Key.AddrHash.Equals(addressHash))
                {
                    sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                    slotKey[..slotPrefixLength].CopyTo(currentPrefixBuf);
                    ReadOnlySpan<byte> currentPrefix = currentPrefixBuf;

                    ref TWriter suffixWriter = ref prefixLevel.BeginValueWrite();
                    using HsstByteTagMapBuilder<TWriter> suffixLevel = new(ref suffixWriter);

                    while (storageIdx < sortedStorages.Count &&
                        sortedStorages[storageIdx].Key.AddrHash.Equals(addressHash))
                    {
                        sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                        if (!slotKey[..slotPrefixLength].SequenceEqual(currentPrefix))
                            break;

                        SlotValue? value = sortedStorages[storageIdx].Value;
                        byte suffixTag = slotKey[slotPrefixLength];
                        if (value.HasValue)
                        {
                            ReadOnlySpan<byte> withoutLeadingZeros = value.Value.AsReadOnlySpan.WithoutLeadingZeros();
                            suffixLevel.Add(suffixTag, withoutLeadingZeros);
                        }
                        else
                        {
                            suffixLevel.Add(suffixTag, []);
                        }
                        if (bloom is not null)
                        {
                            ulong s0 = MemoryMarshal.Read<ulong>(slotKey);
                            ulong s1 = MemoryMarshal.Read<ulong>(slotKey[8..]);
                            ulong s2 = MemoryMarshal.Read<ulong>(slotKey[16..]);
                            ulong s3 = MemoryMarshal.Read<ulong>(slotKey[24..]);
                            bloom.Add(addrBloomKey ^ s0 ^ s1 ^ s2 ^ s3);
                        }
                        storageIdx++;
                    }

                    suffixLevel.Build();
                    prefixLevel.FinishValueWrite(currentPrefix);
                }

                prefixLevel.Build();
                perAddr.FinishValueWrite(PersistedSnapshot.SlotSubTag);
            }

            // Sub-tag 0x05: Account. Present-marker encoding: [0x00] deleted, RLP-bytes
            // present; length 0 = absent (gap-filled). Slim account RLP starts with a
            // list header (0xc0+) so 0x00 first-byte is unambiguous.
            if (address is not null && snapshot.TryGetAccount(address, out Account? account))
            {
                if (account is null)
                {
                    perAddr.Add(PersistedSnapshot.AccountSubTag, [0x00]);
                }
                else
                {
                    int len = AccountDecoder.Slim.GetLength(account);
                    rlpStream.Reset();
                    AccountDecoder.Slim.Encode(rlpStream, account);
                    perAddr.Add(PersistedSnapshot.AccountSubTag, rlpBuffer.AsSpan(0, len));
                }
            }

            // Sub-tag 0x06: Self-destruct. Present-marker encoding: [0x00] destructed,
            // [0x01] new account; length 0 = absent (gap-filled by DenseByteIndex).
            if (address is not null && snapshot.Content.SelfDestructedStorageAddresses.TryGetValue(address, out bool sdValue))
            {
                perAddr.Add(PersistedSnapshot.SelfDestructSubTag, sdValue ? [0x01] : [0x00]);
            }

            perAddr.Build();
            addressLevel.FinishValueWrite(addressHashPrefix);
        }

        addressLevel.Build();
        outer.FinishValueWrite(PersistedSnapshot.AccountColumnTag);
    }

    private static void WriteStateTopNodesColumn<TWriter, TReader, TPin>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot, NativeMemoryList<TreePath> stateNodeKeys, BlobArenaWriter blobWriter, BloomFilter? trieBloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilder<TWriter, TReader, TPin> inner = new(ref innerWriter, new HsstBTreeOptions
        {
            MinSeparatorLength = 3,
        }, expectedKeyCount: stateNodeKeys.Count);
        Span<byte> keyBuffer = stackalloc byte[3];
        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];
        for (int i = 0; i < stateNodeKeys.Count; i++)
        {
            TreePath path = stateNodeKeys[i];
            snapshot.TryGetStateNode(path, out TrieNode? node);
            path.EncodeWith3Byte(keyBuffer);
            ReadOnlySpan<byte> rlp = node!.FullRlp.AsSpan();
            NodeRef nr = blobWriter.WriteRlp(rlp);
            NodeRef.Write(nrBuf, in nr);
            ref TWriter valueWriter = ref inner.BeginValueWrite();
            IByteBufferWriter.Copy(ref valueWriter, nrBuf);
            inner.FinishValueWrite(keyBuffer, NodeRef.Size);
            trieBloom?.Add(PersistedSnapshotBloomBuilder.StatePathKey(in path));
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshot.StateTopNodesTag);
    }

    private static void WriteStateNodesColumnCompact<TWriter, TReader, TPin>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot, NativeMemoryList<TreePath> stateNodeKeys, BlobArenaWriter blobWriter, BloomFilter? trieBloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilder<TWriter, TReader, TPin> inner = new(ref innerWriter, new HsstBTreeOptions
        {
            MinSeparatorLength = 8,
        }, expectedKeyCount: stateNodeKeys.Count);
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
            trieBloom?.Add(PersistedSnapshotBloomBuilder.StatePathKey(in path));
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshot.StateNodeTag);
    }

    private static void WriteStateNodesColumnFallback<TWriter, TReader, TPin>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot, NativeMemoryList<TreePath> stateNodeKeys, BlobArenaWriter blobWriter, BloomFilter? trieBloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilder<TWriter, TReader, TPin> inner = new(ref innerWriter, expectedKeyCount: stateNodeKeys.Count);
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
            trieBloom?.Add(PersistedSnapshotBloomBuilder.StatePathKey(in path));
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshot.StateNodeFallbackTag);
    }

    /// <summary>
    /// Convert a Full snapshot into a Linked snapshot where trie RLP values become
    /// NodeRefs. Metadata column (0x00) copied as-is. Flat state-trie columns (0x03,
    /// 0x05, 0x06) have values replaced with NodeRef(snapshotId, offset). Per-address
    /// column (0x01) is rewritten so its inner storage-trie sub-tags (0x01/0x02) have
    /// their innermost path→RLP values replaced with NodeRefs; the account / slots /
    /// self-destruct sub-tags are copied as-is because those values are small and not
    /// shared across snapshots.
    /// </summary>
    internal static void ConvertFullToLinked<TWriter, TReader, TPin>(PersistedSnapshot fullSnapshot, ref TWriter writer) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using WholeReadSession session = fullSnapshot.BeginWholeReadSession();
        WholeReadSessionReader r = session.GetReader();

        // NodeRef.RlpDataOffset is a 32-bit absolute snapshot offset, so a Full
        // snapshot referenced by NodeRefs cannot exceed int.MaxValue bytes. The
        // per-column int casts below silently rely on this; hoist the check up
        // front so a violation surfaces with snapshot identity instead of a
        // context-free OverflowException deep inside per-column conversion.
        if ((ulong)r.Length > int.MaxValue)
            throw new InvalidOperationException(
                $"ConvertFullToLinked: source Full snapshot id={fullSnapshot.Id} size={r.Length} exceeds the 2 GiB NodeRef addressing limit.");

        using HsstDenseByteIndexBuilder<TWriter> outerBuilder = new(ref writer);

        int snapshotId = fullSnapshot.Id;

        foreach (byte[] tag in s_columnTags)
        {
            HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
            if (!hsst.TrySeek(tag, out Bound columnScope)) continue;

            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();

            switch (tag[0])
            {
                // Metadata: copy as-is
                case 0x00:
                    CopyColumn<TWriter>(in r, columnScope, ref valueWriter);
                    break;
                // Per-address unified column: storage-trie sub-tags 0x01/0x02 get
                // their innermost path→RLP values replaced with NodeRefs; the slots /
                // account / SD sub-tags are small and remain inline.
                case 0x01:
                    ConvertAccountColumnToNodeRefs<TWriter, TReader, TPin>(in r, columnScope, ref valueWriter, snapshotId);
                    break;
                // Flat trie columns: convert values to NodeRefs (PackedArray, key sizes match column build sites)
                case 0x03:
                    ConvertFlatColumnToNodeRefs<TWriter>(in r, columnScope, ref valueWriter, snapshotId, keySize: 8);
                    break;
                case 0x05:
                    ConvertFlatColumnToNodeRefs<TWriter>(in r, columnScope, ref valueWriter, snapshotId, keySize: 3);
                    break;
                case 0x06:
                    ConvertFlatColumnToNodeRefs<TWriter>(in r, columnScope, ref valueWriter, snapshotId, keySize: 33);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown tag 0x{tag[0]:X2}");
            }

            outerBuilder.FinishValueWrite(tag);
        }

        outerBuilder.Build();
    }

    private static void CopyColumn<TWriter>(scoped in WholeReadSessionReader reader, Bound columnScope, ref TWriter writer) where TWriter : IByteBufferWriter =>
        IByteBufferWriter.Copy<TWriter, WholeReadSessionReader, NoOpPin>(ref writer, in reader, columnScope);

    /// <summary>
    /// Convert a flat (non-nested) trie column's values to NodeRefs.
    /// Each entry's RLP value is replaced with a NodeRef pointing back to the Full snapshot.
    /// </summary>
    private static void ConvertFlatColumnToNodeRefs<TWriter>(
        scoped in WholeReadSessionReader reader, Bound columnScope, ref TWriter writer,
        int snapshotId,
        int keySize) where TWriter : IByteBufferWriter
    {
        HsstPackedArrayBuilder<TWriter> builder = new(ref writer, keySize, NodeRef.Size);
        using HsstRefEnumerator<WholeReadSessionReader, NoOpPin> e = new(in reader, columnScope);
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];
        Span<byte> keyBuf = stackalloc byte[Math.Max(1, keySize)];

        while (e.MoveNext())
        {
            KeyValueEntry cur = e.Current;
            // NodeRef points directly at the RLP start; length is recovered from the
            // RLP header on read, so the referenced index doesn't need length metadata.
            // ValueBound.Offset is reader-absolute (snapshot-absolute) since the reader
            // is the snapshot's WholeReadSessionReader — no separate columnOffset add.
            NodeRef.Write(refBytes, new NodeRef(snapshotId, checked((int)cur.ValueBound.Offset)));
            builder.Add(e.CopyCurrentLogicalKey(keyBuf), refBytes);
        }

        builder.Build();
        builder.Dispose();
    }

    /// <summary>
    /// Convert a nested trie column (storage nodes) to NodeRefs.
    /// Outer keys (address hash prefixes) are preserved. Inner values are replaced with NodeRefs.
    /// </summary>
    private static void ConvertNestedColumnToNodeRefs<TWriter, TWriterReader, TWriterPin>(
        scoped in WholeReadSessionReader reader, Bound columnScope, ref TWriter writer,
        int snapshotId,
        int outerMinSep = 0, int innerKeySize = 0) where TWriter : IByteBufferWriterWithReader<TWriterReader, TWriterPin> where TWriterReader : IHsstByteReader<TWriterPin>, allows ref struct where TWriterPin : struct, IBufferPin, allows ref struct
    {
        HsstBTreeBuilder<TWriter, TWriterReader, TWriterPin> builder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = outerMinSep });
        using HsstRefEnumerator<WholeReadSessionReader, NoOpPin> outerEnum = new(in reader, columnScope);
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];
        Span<byte> innerKeyBuf = stackalloc byte[Math.Max(1, innerKeySize)];
        // Outer (BTree) keys are storage-trie path prefixes — bounded ≤33; 64 is safe.
        Span<byte> outerKeyBuf = stackalloc byte[64];

        while (outerEnum.MoveNext())
        {
            Bound innerScope = outerEnum.Current.ValueBound;
            ReadOnlySpan<byte> outerKey = outerEnum.CopyCurrentLogicalKey(outerKeyBuf);

            ref TWriter innerWriter = ref builder.BeginValueWrite();
            HsstPackedArrayBuilder<TWriter> innerBuilder = new(ref innerWriter, innerKeySize, NodeRef.Size);
            using HsstRefEnumerator<WholeReadSessionReader, NoOpPin> innerEnum = new(in reader, innerScope);

            while (innerEnum.MoveNext())
            {
                KeyValueEntry inner = innerEnum.Current;
                // NodeRef points directly at the RLP start (absolute snapshot offset).
                NodeRef.Write(refBytes, new NodeRef(snapshotId, checked((int)inner.ValueBound.Offset)));
                innerBuilder.Add(innerEnum.CopyCurrentLogicalKey(innerKeyBuf), refBytes);
            }

            innerBuilder.Build();
            innerBuilder.Dispose();
            builder.FinishValueWrite(outerKey);
        }

        builder.Build();
        builder.Dispose();
    }

    /// <summary>
    /// Convert column 0x01 (per-address) for a Full→Linked rewrite. Outer (BTree on
    /// 20-byte address-hash prefix) and inner DenseByteIndex layouts are preserved;
    /// only the storage-trie sub-tags (0x01 top, 0x02 compact, 0x03 fallback) have their
    /// inner HSST values rewritten as NodeRefs pointing back into the source Full
    /// snapshot's column 0x01 region. Sub-tags 0x04 (slots) / 0x05 (account RLP) / 0x06
    /// (SD) are copied as-is — they're small inline values and aren't shared across
    /// snapshots.
    /// </summary>
    private static void ConvertAccountColumnToNodeRefs<TWriter, TWriterReader, TWriterPin>(
        scoped in WholeReadSessionReader reader, Bound columnScope, ref TWriter writer,
        int snapshotId) where TWriter : IByteBufferWriterWithReader<TWriterReader, TWriterPin> where TWriterReader : IHsstByteReader<TWriterPin>, allows ref struct where TWriterPin : struct, IBufferPin, allows ref struct
    {
        using HsstBTreeBuilder<TWriter, TWriterReader, TWriterPin> outerBuilder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = 4 });
        using HsstRefEnumerator<WholeReadSessionReader, NoOpPin> outerEnum = new(in reader, columnScope);
        // Outer key is a 20-byte address hash.
        Span<byte> outerKeyBuf = stackalloc byte[32];

        while (outerEnum.MoveNext())
        {
            Bound perAddrScope = outerEnum.Current.ValueBound;

            ref TWriter perAddrWriter = ref outerBuilder.BeginValueWrite();
            using HsstDenseByteIndexBuilder<TWriter> perAddrBuilder = new(ref perAddrWriter);

            // Sub-tag 0x01: storage trie top. Inner HSST values become NodeRefs.
            HsstReader<WholeReadSessionReader, NoOpPin> top = new(in reader, perAddrScope);
            if (top.TrySeek(PersistedSnapshot.StorageTopSubTag, out Bound topBound) && topBound.Length > 0)
            {
                ref TWriter subWriter = ref perAddrBuilder.BeginValueWrite();
                ConvertStorageTrieSubTagToNodeRefs<TWriter>(
                    in reader, topBound,
                    ref subWriter, snapshotId, innerKeySize: 3);
                perAddrBuilder.FinishValueWrite(PersistedSnapshot.StorageTopSubTag);
            }

            // Sub-tag 0x02: storage trie compact. Same conversion, 8-byte path keys.
            HsstReader<WholeReadSessionReader, NoOpPin> compact = new(in reader, perAddrScope);
            if (compact.TrySeek(PersistedSnapshot.StorageCompactSubTag, out Bound compactBound) && compactBound.Length > 0)
            {
                ref TWriter subWriter = ref perAddrBuilder.BeginValueWrite();
                ConvertStorageTrieSubTagToNodeRefs<TWriter>(
                    in reader, compactBound,
                    ref subWriter, snapshotId, innerKeySize: 8);
                perAddrBuilder.FinishValueWrite(PersistedSnapshot.StorageCompactSubTag);
            }

            // Sub-tag 0x03: storage trie fallback. Same conversion, 33-byte path keys.
            HsstReader<WholeReadSessionReader, NoOpPin> fallback = new(in reader, perAddrScope);
            if (fallback.TrySeek(PersistedSnapshot.StorageFallbackSubTag, out Bound fallbackBound) && fallbackBound.Length > 0)
            {
                ref TWriter subWriter = ref perAddrBuilder.BeginValueWrite();
                ConvertStorageTrieSubTagToNodeRefs<TWriter>(
                    in reader, fallbackBound,
                    ref subWriter, snapshotId, innerKeySize: 33);
                perAddrBuilder.FinishValueWrite(PersistedSnapshot.StorageFallbackSubTag);
            }

            // Sub-tag 0x04: slots — copy bytes as-is. Slot values are inline, not NodeRefs.
            HsstReader<WholeReadSessionReader, NoOpPin> slot = new(in reader, perAddrScope);
            if (slot.TrySeek(PersistedSnapshot.SlotSubTag, out Bound slotBound) && slotBound.Length > 0)
            {
                using NoOpPin pin = reader.PinBuffer(slotBound.Offset, slotBound.Length);
                perAddrBuilder.Add(PersistedSnapshot.SlotSubTag, pin.Buffer);
            }

            // Sub-tag 0x05: account RLP — inline.
            HsstReader<WholeReadSessionReader, NoOpPin> acct = new(in reader, perAddrScope);
            if (acct.TrySeek(PersistedSnapshot.AccountSubTag, out Bound acctBound) && acctBound.Length > 0)
            {
                using NoOpPin pin = reader.PinBuffer(acctBound.Offset, acctBound.Length);
                perAddrBuilder.Add(PersistedSnapshot.AccountSubTag, pin.Buffer);
            }

            // Sub-tag 0x06: self-destruct flag — inline.
            HsstReader<WholeReadSessionReader, NoOpPin> sd = new(in reader, perAddrScope);
            if (sd.TrySeek(PersistedSnapshot.SelfDestructSubTag, out Bound sdBound) && sdBound.Length > 0)
            {
                using NoOpPin pin = reader.PinBuffer(sdBound.Offset, sdBound.Length);
                perAddrBuilder.Add(PersistedSnapshot.SelfDestructSubTag, pin.Buffer);
            }

            perAddrBuilder.Build();
            outerBuilder.FinishValueWrite(outerEnum.CopyCurrentLogicalKey(outerKeyBuf));
        }

        outerBuilder.Build();
    }

    private static void ConvertStorageTrieSubTagToNodeRefs<TWriter>(
        scoped in WholeReadSessionReader reader, Bound subTagScope,
        ref TWriter writer, int snapshotId, int innerKeySize) where TWriter : IByteBufferWriter
    {
        // The sub-tag value is itself an inner HSST(BTree) of (path → RLP). Walk every
        // entry, replacing RLP with a NodeRef whose RlpDataOffset points at the RLP
        // start in the source Full snapshot's column 0x01 region (length is recovered
        // from the RLP header on read).
        HsstPackedArrayBuilder<TWriter> innerBuilder = new(ref writer, innerKeySize, NodeRef.Size);
        using HsstRefEnumerator<WholeReadSessionReader, NoOpPin> innerEnum = new(in reader, subTagScope);
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];
        Span<byte> keyBuf = stackalloc byte[Math.Max(1, innerKeySize)];

        while (innerEnum.MoveNext())
        {
            KeyValueEntry inner = innerEnum.Current;
            NodeRef.Write(refBytes, new NodeRef(snapshotId, checked((int)inner.ValueBound.Offset)));
            innerBuilder.Add(innerEnum.CopyCurrentLogicalKey(keyBuf), refBytes);
        }

        innerBuilder.Build();
        innerBuilder.Dispose();
    }

    /// <summary>
    /// N-way merge of N persisted snapshots (oldest-first) into output buffer.
    /// Pre-converts all Full snapshots to Linked so the merge only handles Linked snapshots
    /// (all trie values are already NodeRefs). This eliminates the dual code path in trie merges.
    /// </summary>
    internal static void NWayMergeSnapshots<TWriter, TReader, TPin>(PersistedSnapshotList snapshots, ref TWriter writer, HashSet<int> referencedBlobArenaIds, BloomFilter? bloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // All snapshots are blob-backed (values in trie columns are NodeRefs), so we can
        // merge them directly without any Full→Linked pre-conversion stage.
        using HsstDenseByteIndexBuilder<TWriter> outerBuilder = new(ref writer);

        foreach (byte[] tag in s_columnTags)
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            switch (tag[0])
            {
                case 0x00:
                    NWayMetadataMerge<TWriter, TReader, TPin>(snapshots, ref valueWriter, referencedBlobArenaIds);
                    break;
                case 0x01:
                    NWayMergeAccountColumn<TWriter, TReader, TPin>(snapshots, tag, ref valueWriter, bloom);
                    break;
                case 0x03:
                    NWayStreamingMerge<TWriter, TReader, TPin>(snapshots, tag, ref valueWriter, keySize: 8);
                    break;
                case 0x05:
                    NWayStreamingMerge<TWriter, TReader, TPin>(snapshots, tag, ref valueWriter, keySize: 3);
                    break;
                case 0x06:
                    NWayStreamingMerge<TWriter, TReader, TPin>(snapshots, tag, ref valueWriter, keySize: 33);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown tag 0x{tag[0]:X2}");
            }
            outerBuilder.FinishValueWrite(tag);
        }

        outerBuilder.Build();
    }

    private static int SpanOffset(ReadOnlySpan<byte> outer, ReadOnlySpan<byte> inner) =>
        inner.IsEmpty ? 0 : (int)Unsafe.ByteOffset(
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(outer)),
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(inner)));

    // --- N-Way merge methods ---

    /// <summary>
    /// N-way streaming merge of a column across N snapshots. On key collision, newest (highest index) wins.
    /// Uses <see cref="HsstEnumerator"/> for zero-allocation cursor-based enumeration.
    /// </summary>
    internal static void NWayStreamingMerge<TWriter, TReader, TPin>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer,
        int keySize) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstEnumerator> enums = new(n, n);
        using ArrayPoolList<bool> hasMore = new(n, n);
        using ArrayPoolList<(long Offset, long Length)> columnBounds = new(n, n);
        using ArrayPoolList<WholeReadSession> sessions = new(n, n);

        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                WholeReadSessionReader r = sessions[i].GetReader();
                HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
                columnBounds[i] = hsst.TrySeek(tag, out Bound cb) ? (cb.Offset, cb.Length) : (0, 0);
                enums[i] = new HsstEnumerator(in r, new Bound(columnBounds[i].Offset, columnBounds[i].Length));
                hasMore[i] = enums[i].MoveNext(in r);
            }

            using HsstPackedArrayBuilder<TWriter> builder = new(ref writer, keySize, NodeRef.Size);

            // HsstEnumerator.CopyCurrentLogicalKey returns lex/BE bytes regardless of the
            // source PackedArray's storage layout (BE-stored or LE-stored). That's the
            // form HsstPackedArrayBuilder.Add expects, so the merge needs no per-keysize
            // branching.
            Span<byte> iKeyLogical = stackalloc byte[Math.Max(1, keySize)];
            Span<byte> mKeyLogical = stackalloc byte[Math.Max(1, keySize)];
            Span<byte> minKeyLogical = stackalloc byte[Math.Max(1, keySize)];

            while (true)
            {
                // Find min key across all active enumerators, newest wins on tie. Each
                // comparison pins both keys via the source reader; for span-backed readers
                // (NoOpPin) the pins are zero-cost.
                int minIdx = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    if (minIdx < 0)
                    {
                        minIdx = i;
                        continue;
                    }
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    WholeReadSessionReader rM = sessions[minIdx].GetReader();
                    ReadOnlySpan<byte> kI = enums[i].CopyCurrentLogicalKey(in rI, iKeyLogical);
                    ReadOnlySpan<byte> kM = enums[minIdx].CopyCurrentLogicalKey(in rM, mKeyLogical);
                    int cmp = kI.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = i;
                    else if (cmp == 0) minIdx = i; // newer (higher index) wins
                }

                if (minIdx < 0) break;

                Bound valBound = enums[minIdx].CurrentValue;
                WholeReadSessionReader minIdxReader = sessions[minIdx].GetReader();
                using NoOpPin valPin = minIdxReader.PinBuffer(valBound.Offset, valBound.Length);
                ReadOnlySpan<byte> minKey = enums[minIdx].CopyCurrentLogicalKey(in minIdxReader, minKeyLogical);
                builder.Add(minKey, valPin.Buffer);

                for (int i = 0; i < n; i++)
                {
                    if (i == minIdx || !hasMore[i]) continue;
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    ReadOnlySpan<byte> kI = enums[i].CopyCurrentLogicalKey(in rI, iKeyLogical);
                    if (kI.SequenceCompareTo(minKey) == 0)
                    {
                        hasMore[i] = enums[i].MoveNext(in rI);
                    }
                }
                {
                    WholeReadSessionReader r = sessions[minIdx].GetReader();
                    hasMore[minIdx] = enums[minIdx].MoveNext(in r);
                }
            }

            builder.Build();
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i].Dispose();
            for (int i = 0; i < n; i++) sessions[i]?.Dispose();
        }
    }

    /// <summary>
    /// N-way nested streaming merge: outer keys merged across N sources,
    /// when M sources share an outer key their inner HSST values are merged via NWayStreamingMerge.
    /// Single-source keys are copied as-is.
    /// </summary>
    internal static void NWayNestedStreamingMerge<TWriter, TReader, TPin>(
        HsstEnumerator[] enums, bool[] hasMore, int n,
        WholeReadSession[] sessions,
        ref TWriter writer,
        int outerMinSep = 0, int innerMinSep = 0,
        bool innerByteTagMap = false) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = outerMinSep });

        // Temp list for collecting matching source indices
        using ArrayPoolList<int> matchingSourcesList = new(n, n);
        int[] matchingSources = matchingSourcesList.UnsafeGetInternalArray();

        // 64 covers every key size that ends up in this merge: storage-hash address
        // prefixes (≤32) and storage path prefixes for the BTree variants (≤33).
        Span<byte> iKeyBuf = stackalloc byte[64];
        Span<byte> mKeyBuf = stackalloc byte[64];
        Span<byte> minKeyBuf = stackalloc byte[64];

        while (true)
        {
            int minIdx = -1;
            for (int i = 0; i < n; i++)
            {
                if (!hasMore[i]) continue;
                if (minIdx < 0)
                {
                    minIdx = i;
                    continue;
                }
                WholeReadSessionReader rI = sessions[i].GetReader();
                WholeReadSessionReader rM = sessions[minIdx].GetReader();
                ReadOnlySpan<byte> kI = enums[i].CopyCurrentLogicalKey(in rI, iKeyBuf);
                ReadOnlySpan<byte> kM = enums[minIdx].CopyCurrentLogicalKey(in rM, mKeyBuf);
                int cmp = kI.SequenceCompareTo(kM);
                if (cmp < 0) minIdx = i;
            }

            if (minIdx < 0) break;

            WholeReadSessionReader minIdxReader = sessions[minIdx].GetReader();
            ReadOnlySpan<byte> minKey = enums[minIdx].CopyCurrentLogicalKey(in minIdxReader, minKeyBuf);

            // Collect all sources with this key
            int matchCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (!hasMore[i]) continue;
                WholeReadSessionReader rI = sessions[i].GetReader();
                ReadOnlySpan<byte> kI = enums[i].CopyCurrentLogicalKey(in rI, iKeyBuf);
                if (kI.SequenceCompareTo(minKey) == 0)
                    matchingSources[matchCount++] = i;
            }

            if (matchCount == 1)
            {
                // Single source: copy as-is
                int srcIdx = matchingSources[0];
                Bound vb = enums[srcIdx].CurrentValue;
                WholeReadSessionReader srcReader = sessions[srcIdx].GetReader();
                using NoOpPin valPin = srcReader.PinBuffer(vb.Offset, vb.Length);
                builder.Add(minKey, valPin.Buffer);
            }
            else
            {
                // M sources: create M inner enumerators and merge
                ref TWriter innerWriter = ref builder.BeginValueWrite();
                NWayInnerMerge<TWriter, TReader, TPin>(enums, matchingSources, matchCount, sessions,
                    ref innerWriter, innerMinSep, innerByteTagMap);
                builder.FinishValueWrite(minKey);
            }

            // Advance all matching
            for (int j = 0; j < matchCount; j++)
            {
                int i = matchingSources[j];
                WholeReadSessionReader r = sessions[i].GetReader();
                hasMore[i] = enums[i].MoveNext(in r);
            }
        }

        builder.Build();
    }

    /// <summary>
    /// Merge inner HSST values from M sources (identified by matchingSources indices).
    /// Each source's current value (from outer enumerator) is an inner HSST.
    /// Creates M inner MergeEnumerators and performs N-way merge with newest-wins.
    /// </summary>
    private static void NWayInnerMerge<TWriter, TReader, TPin>(
        HsstEnumerator[] outerEnums, int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        ref TWriter writer,
        int minSeparatorLength = 0,
        bool useByteTagMap = false) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using ArrayPoolList<HsstEnumerator> innerEnums = new(matchCount, matchCount);
        using ArrayPoolList<bool> innerHasMore = new(matchCount, matchCount);
        // innerBounds are snapshot-absolute (offset within snapshot, length).
        using ArrayPoolList<(long Offset, long Length)> innerBounds = new(matchCount, matchCount);

        try
        {
            for (int j = 0; j < matchCount; j++)
            {
                int srcIdx = matchingSources[j];
                Bound vb = outerEnums[srcIdx].CurrentValue;
                innerBounds[j] = (vb.Offset, vb.Length);
                WholeReadSessionReader r = sessions[srcIdx].GetReader();
                innerEnums[j] = new HsstEnumerator(in r, new Bound(innerBounds[j].Offset, innerBounds[j].Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
            }

            if (useByteTagMap)
                MergeIntoByteTagMap<TWriter, TReader, TPin>(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions, ref writer);
            else
                MergeIntoBTree<TWriter, TReader, TPin>(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions, ref writer, minSeparatorLength);
        }
        finally
        {
            for (int j = 0; j < matchCount; j++) innerEnums[j].Dispose();
        }
    }

    private static int PickMinIdx(ArrayPoolList<HsstEnumerator> innerEnums, ArrayPoolList<bool> innerHasMore, ArrayPoolList<(long Offset, long Length)> innerBounds, int[] matchingSources, int matchCount, WholeReadSession[] sessions)
    {
        Span<byte> bufJ = stackalloc byte[64];
        Span<byte> bufM = stackalloc byte[64];
        int minIdx = -1;
        for (int j = 0; j < matchCount; j++)
        {
            if (!innerHasMore[j]) continue;
            if (minIdx < 0) { minIdx = j; continue; }
            WholeReadSessionReader rJ = sessions[matchingSources[j]].GetReader();
            WholeReadSessionReader rM = sessions[matchingSources[minIdx]].GetReader();
            ReadOnlySpan<byte> kJ = innerEnums[j].CopyCurrentLogicalKey(in rJ, bufJ);
            ReadOnlySpan<byte> kM = innerEnums[minIdx].CopyCurrentLogicalKey(in rM, bufM);
            int cmp = kJ.SequenceCompareTo(kM);
            if (cmp < 0) minIdx = j;
            else if (cmp == 0) minIdx = j; // newer (higher j = higher source index) wins
        }
        return minIdx;
    }

    private static void AdvanceMatching(ArrayPoolList<HsstEnumerator> innerEnums, ArrayPoolList<bool> innerHasMore, ArrayPoolList<(long Offset, long Length)> innerBounds, int[] matchingSources, int matchCount, WholeReadSession[] sessions, int minIdx, ReadOnlySpan<byte> minKey)
    {
        Span<byte> bufJ = stackalloc byte[64];
        for (int j = 0; j < matchCount; j++)
        {
            if (j == minIdx || !innerHasMore[j]) continue;
            WholeReadSessionReader rJ = sessions[matchingSources[j]].GetReader();
            ReadOnlySpan<byte> kJ = innerEnums[j].CopyCurrentLogicalKey(in rJ, bufJ);
            if (kJ.SequenceCompareTo(minKey) == 0)
                innerHasMore[j] = innerEnums[j].MoveNext(in rJ);
        }
        WholeReadSessionReader rMin = sessions[matchingSources[minIdx]].GetReader();
        innerHasMore[minIdx] = innerEnums[minIdx].MoveNext(in rMin);
    }

    private static void MergeIntoBTree<TWriter, TReader, TPin>(
        ArrayPoolList<HsstEnumerator> innerEnums, ArrayPoolList<bool> innerHasMore,
        ArrayPoolList<(long Offset, long Length)> innerBounds,
        int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        ref TWriter writer, int minSeparatorLength) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = minSeparatorLength });
        Span<byte> minKeyBuf = stackalloc byte[64];
        while (true)
        {
            int minIdx = PickMinIdx(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions);
            if (minIdx < 0) break;

            Bound vb = innerEnums[minIdx].CurrentValue;
            WholeReadSessionReader r = sessions[matchingSources[minIdx]].GetReader();
            ReadOnlySpan<byte> minKey = innerEnums[minIdx].CopyCurrentLogicalKey(in r, minKeyBuf);
            using NoOpPin valPin = r.PinBuffer(vb.Offset, vb.Length);
            builder.Add(minKey, valPin.Buffer);
            AdvanceMatching(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions, minIdx, minKey);
        }
        builder.Build();
    }

    private static void MergeIntoByteTagMap<TWriter, TReader, TPin>(
        ArrayPoolList<HsstEnumerator> innerEnums, ArrayPoolList<bool> innerHasMore,
        ArrayPoolList<(long Offset, long Length)> innerBounds,
        int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        ref TWriter writer) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using HsstByteTagMapBuilder<TWriter> builder = new(ref writer);
        // ByteTagMap keys are 1 byte; one extra slot keeps the buffer comfortably bigger.
        Span<byte> minKeyBuf = stackalloc byte[8];
        while (true)
        {
            int minIdx = PickMinIdx(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions);
            if (minIdx < 0) break;

            Bound vb = innerEnums[minIdx].CurrentValue;
            WholeReadSessionReader r = sessions[matchingSources[minIdx]].GetReader();
            ReadOnlySpan<byte> minKey = innerEnums[minIdx].CopyCurrentLogicalKey(in r, minKeyBuf);
            using NoOpPin valPin = r.PinBuffer(vb.Offset, vb.Length);
            builder.Add(minKey[0], valPin.Buffer);
            AdvanceMatching(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions, minIdx, minKey);
        }
        builder.Build();
    }

    /// <summary>
    /// N-way nested streaming merge across N persisted snapshots.
    /// Initializes enumerators from snapshot data and delegates to the core merge method.
    /// </summary>
    internal static void NWayNestedStreamingMerge<TWriter, TReader, TPin>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer,
        int outerMinSep = 0, int innerMinSep = 0) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstEnumerator> enumsList = new(n, n);
        using ArrayPoolList<bool> hasMoreList = new(n, n);
        using ArrayPoolList<(long Offset, long Length)> columnBoundsList = new(n, n);
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        HsstEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        bool[] hasMore = hasMoreList.UnsafeGetInternalArray();
        (long Offset, long Length)[] columnBounds = columnBoundsList.UnsafeGetInternalArray();
        WholeReadSession[] sessions = sessionsList.UnsafeGetInternalArray();

        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                WholeReadSessionReader r = sessions[i].GetReader();
                HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
                columnBounds[i] = hsst.TrySeek(tag, out Bound cb) ? (cb.Offset, cb.Length) : (0, 0);
                enums[i] = new HsstEnumerator(in r, new Bound(columnBounds[i].Offset, columnBounds[i].Length));
                hasMore[i] = enums[i].MoveNext(in r);
            }

            NWayNestedStreamingMerge<TWriter, TReader, TPin>(enums, hasMore, n, sessions,
                ref writer, outerMinSep, innerMinSep);
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i].Dispose();
            for (int i = 0; i < n; i++) sessions[i]?.Dispose();
        }
    }

    /// <summary>
    /// Trie-specific nested streaming merge for storage trie columns (0x07/0x08). Outer
    /// (storage hash prefix) keeps the BTree layout; inner (TreePath -> NodeRef) is built
    /// as a fixed-size PackedArray since both inner key and value (NodeRef) are fixed.
    /// </summary>
    internal static void NWayNestedStreamingMergeTrie<TWriter, TReader, TPin>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer,
        int outerMinSep, int innerKeySize) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstEnumerator> enumsList = new(n, n);
        using ArrayPoolList<bool> hasMoreList = new(n, n);
        using ArrayPoolList<(long Offset, long Length)> columnBoundsList = new(n, n);
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        using ArrayPoolList<int> matchingSourcesList = new(n, n);
        HsstEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        bool[] hasMore = hasMoreList.UnsafeGetInternalArray();
        (long Offset, long Length)[] columnBounds = columnBoundsList.UnsafeGetInternalArray();
        WholeReadSession[] sessions = sessionsList.UnsafeGetInternalArray();
        int[] matchingSources = matchingSourcesList.UnsafeGetInternalArray();

        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                WholeReadSessionReader r = sessions[i].GetReader();
                HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
                columnBounds[i] = hsst.TrySeek(tag, out Bound cb) ? (cb.Offset, cb.Length) : (0, 0);
                enums[i] = new HsstEnumerator(in r, new Bound(columnBounds[i].Offset, columnBounds[i].Length));
                hasMore[i] = enums[i].MoveNext(in r);
            }

            using HsstBTreeBuilder<TWriter, TReader, TPin> outerBuilder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = outerMinSep });

            // Outer keys are storage-hash address prefixes (≤32 bytes); 64 is plenty.
            Span<byte> iKeyBuf = stackalloc byte[64];
            Span<byte> mKeyBuf = stackalloc byte[64];
            Span<byte> minKeyBuf = stackalloc byte[64];

            while (true)
            {
                int minIdx = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    if (minIdx < 0) { minIdx = i; continue; }
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    WholeReadSessionReader rM = sessions[minIdx].GetReader();
                    ReadOnlySpan<byte> kI = enums[i].CopyCurrentLogicalKey(in rI, iKeyBuf);
                    ReadOnlySpan<byte> kM = enums[minIdx].CopyCurrentLogicalKey(in rM, mKeyBuf);
                    int cmp = kI.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = i;
                }
                if (minIdx < 0) break;

                WholeReadSessionReader minIdxReader = sessions[minIdx].GetReader();
                ReadOnlySpan<byte> minKey = enums[minIdx].CopyCurrentLogicalKey(in minIdxReader, minKeyBuf);

                int matchCount = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    ReadOnlySpan<byte> kI = enums[i].CopyCurrentLogicalKey(in rI, iKeyBuf);
                    if (kI.SequenceCompareTo(minKey) == 0)
                        matchingSources[matchCount++] = i;
                }

                if (matchCount == 1)
                {
                    int srcIdx = matchingSources[0];
                    Bound vb = enums[srcIdx].CurrentValue;
                    WholeReadSessionReader srcReader = sessions[srcIdx].GetReader();
                    using NoOpPin valPin = srcReader.PinBuffer(vb.Offset, vb.Length);
                    outerBuilder.Add(minKey, valPin.Buffer);
                }
                else
                {
                    ref TWriter innerWriter = ref outerBuilder.BeginValueWrite();
                    NWayInnerMergeTrie<TWriter, TReader, TPin>(enums, matchingSources, matchCount, sessions,
                        ref innerWriter, innerKeySize);
                    outerBuilder.FinishValueWrite(minKey);
                }

                for (int j = 0; j < matchCount; j++)
                {
                    int i = matchingSources[j];
                    WholeReadSessionReader r = sessions[i].GetReader();
                    hasMore[i] = enums[i].MoveNext(in r);
                }
            }

            outerBuilder.Build();
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i].Dispose();
            for (int i = 0; i < n; i++) sessions[i]?.Dispose();
        }
    }

    /// <summary>
    /// Trie-specific inner merge: M sources share an outer key; merge their inner trie HSSTs
    /// (TreePath -> NodeRef, fixed-size both sides) into a single PackedArray.
    /// </summary>
    private static void NWayInnerMergeTrie<TWriter, TReader, TPin>(
        HsstEnumerator[] outerEnums, int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        ref TWriter writer,
        int keySize) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using ArrayPoolList<HsstEnumerator> innerEnums = new(matchCount, matchCount);
        using ArrayPoolList<bool> innerHasMore = new(matchCount, matchCount);
        // innerBounds are snapshot-absolute.
        using ArrayPoolList<(long Offset, long Length)> innerBounds = new(matchCount, matchCount);

        try
        {
            for (int j = 0; j < matchCount; j++)
            {
                int srcIdx = matchingSources[j];
                Bound vb = outerEnums[srcIdx].CurrentValue;
                innerBounds[j] = (vb.Offset, vb.Length);
                WholeReadSessionReader r = sessions[srcIdx].GetReader();
                innerEnums[j] = new HsstEnumerator(in r, new Bound(innerBounds[j].Offset, innerBounds[j].Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
            }

            using HsstPackedArrayBuilder<TWriter> builder = new(ref writer, keySize, NodeRef.Size);

            // Inner keys: trie path (fixed PackedArray, keySize ≤ 33). 64 is safe.
            Span<byte> jKeyBuf = stackalloc byte[64];
            Span<byte> mKeyBuf = stackalloc byte[64];
            Span<byte> minKeyBuf = stackalloc byte[64];

            while (true)
            {
                int minIdx = -1;
                for (int j = 0; j < matchCount; j++)
                {
                    if (!innerHasMore[j]) continue;
                    if (minIdx < 0) { minIdx = j; continue; }
                    WholeReadSessionReader rJ = sessions[matchingSources[j]].GetReader();
                    WholeReadSessionReader rM = sessions[matchingSources[minIdx]].GetReader();
                    ReadOnlySpan<byte> kJ = innerEnums[j].CopyCurrentLogicalKey(in rJ, jKeyBuf);
                    ReadOnlySpan<byte> kM = innerEnums[minIdx].CopyCurrentLogicalKey(in rM, mKeyBuf);
                    int cmp = kJ.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = j;
                    else if (cmp == 0) minIdx = j; // newer wins
                }
                if (minIdx < 0) break;

                Bound vb2 = innerEnums[minIdx].CurrentValue;
                WholeReadSessionReader minReader = sessions[matchingSources[minIdx]].GetReader();
                ReadOnlySpan<byte> minKey = innerEnums[minIdx].CopyCurrentLogicalKey(in minReader, minKeyBuf);
                using NoOpPin valPin = minReader.PinBuffer(vb2.Offset, vb2.Length);
                builder.Add(minKey, valPin.Buffer);

                for (int j = 0; j < matchCount; j++)
                {
                    if (j == minIdx || !innerHasMore[j]) continue;
                    WholeReadSessionReader jr = sessions[matchingSources[j]].GetReader();
                    ReadOnlySpan<byte> kJ = innerEnums[j].CopyCurrentLogicalKey(in jr, jKeyBuf);
                    if (kJ.SequenceCompareTo(minKey) == 0)
                        innerHasMore[j] = innerEnums[j].MoveNext(in jr);
                }
                {
                    WholeReadSessionReader mr = sessions[matchingSources[minIdx]].GetReader();
                    innerHasMore[minIdx] = innerEnums[minIdx].MoveNext(in mr);
                }
            }

            builder.Build();
        }
        finally
        {
            for (int j = 0; j < matchCount; j++) innerEnums[j].Dispose();
        }
    }

    /// <summary>
    /// N-way merge of the account column (tag 0x01) across N snapshots.
    /// Outer: 20-byte address keys (minSep=4). For matching addresses with M sources,
    /// calls <see cref="NWayMergePerAddressHsst"/>. Single source: copy as-is.
    /// </summary>
    internal static void NWayMergeAccountColumn<TWriter, TReader, TPin>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer, BloomFilter? bloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstEnumerator> enumsList = new(n, n);
        using ArrayPoolList<bool> hasMoreList = new(n, n);
        using ArrayPoolList<(long Offset, long Length)> columnBoundsList = new(n, n);
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        using ArrayPoolList<int> matchingSourcesList = new(n, n);
        HsstEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        bool[] hasMore = hasMoreList.UnsafeGetInternalArray();
        (long Offset, long Length)[] columnBounds = columnBoundsList.UnsafeGetInternalArray();
        WholeReadSession[] sessions = sessionsList.UnsafeGetInternalArray();
        int[] matchingSources = matchingSourcesList.UnsafeGetInternalArray();

        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                WholeReadSessionReader r = sessions[i].GetReader();
                HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
                columnBounds[i] = hsst.TrySeek(tag, out Bound cb) ? (cb.Offset, cb.Length) : (0, 0);
                enums[i] = new HsstEnumerator(in r, new Bound(columnBounds[i].Offset, columnBounds[i].Length));
                hasMore[i] = enums[i].MoveNext(in r);
            }

            using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = 4 });

            // Outer keys are 20-byte address hashes; 32 covers comfortably.
            Span<byte> iKeyBuf = stackalloc byte[32];
            Span<byte> mKeyBuf = stackalloc byte[32];
            Span<byte> minKeyBuf = stackalloc byte[32];

            while (true)
            {
                int minIdx = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    if (minIdx < 0)
                    {
                        minIdx = i;
                        continue;
                    }
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    WholeReadSessionReader rM = sessions[minIdx].GetReader();
                    ReadOnlySpan<byte> kI = enums[i].CopyCurrentLogicalKey(in rI, iKeyBuf);
                    ReadOnlySpan<byte> kM = enums[minIdx].CopyCurrentLogicalKey(in rM, mKeyBuf);
                    int cmp = kI.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = i;
                }

                if (minIdx < 0) break;

                WholeReadSessionReader minIdxReader = sessions[minIdx].GetReader();
                ReadOnlySpan<byte> minKey = enums[minIdx].CopyCurrentLogicalKey(in minIdxReader, minKeyBuf);

                int matchCount = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    ReadOnlySpan<byte> kI = enums[i].CopyCurrentLogicalKey(in rI, iKeyBuf);
                    if (kI.SequenceCompareTo(minKey) == 0)
                        matchingSources[matchCount++] = i;
                }

                if (matchCount == 1)
                {
                    int srcIdx = matchingSources[0];
                    Bound vb = enums[srcIdx].CurrentValue;
                    WholeReadSessionReader srcReader = sessions[srcIdx].GetReader();
                    using NoOpPin perAddrPin = srcReader.PinBuffer(vb.Offset, vb.Length);
                    ReadOnlySpan<byte> perAddrHsst = perAddrPin.Buffer;
                    builder.Add(minKey, perAddrHsst);
                    if (bloom is not null)
                    {
                        ulong addrKey = MemoryMarshal.Read<ulong>(minKey);
                        bloom.Add(addrKey);
                        HsstReader<WholeReadSessionReader, NoOpPin> slot = new(in srcReader, vb);
                        if (slot.TrySeek(PersistedSnapshot.SlotSubTag, out Bound slotBound))
                            AddSlotKeysToBloom<WholeReadSessionReader, NoOpPin>(in srcReader, slotBound, addrKey, bloom);
                    }
                }
                else
                {
                    // M sources share this address: merge per-address HSSTs
                    ref TWriter perAddrWriter = ref builder.BeginValueWrite();
                    ulong addrKey = 0;
                    if (bloom is not null)
                    {
                        addrKey = MemoryMarshal.Read<ulong>(minKey);
                        bloom.Add(addrKey);
                    }
                    NWayMergePerAddressHsst<TWriter, TReader, TPin>(
                        enums, matchingSources, matchCount, sessions,
                        ref perAddrWriter, bloom, addrKey);
                    builder.FinishValueWrite(minKey);
                }

                for (int j = 0; j < matchCount; j++)
                {
                    int i = matchingSources[j];
                    WholeReadSessionReader r = sessions[i].GetReader();
                    hasMore[i] = enums[i].MoveNext(in r);
                }
            }

            builder.Build();
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i].Dispose();
            for (int i = 0; i < n; i++) sessions[i]?.Dispose();
        }
    }

    /// <summary>
    /// N-way merge of per-address HSSTs from M sources (oldest-first by matchingSources order).
    /// Sub-tags emitted in ascending byte order so the DenseByteIndex builder accepts them:
    /// - 0x01 StorageTop: streaming merge of inner (3-byte path → NodeRef) PackedArrays.
    ///   No destruct barrier — orphan nodes are unreachable from the new storage root.
    /// - 0x02 StorageCompact: same as 0x01 with 8-byte path keys.
    /// - 0x03 StorageFallback: same as 0x01 with 33-byte path keys.
    /// - 0x04 Slots: find newest destruct barrier, merge slots from barrier..M-1 via nested streaming merge
    /// - 0x05 Account: newest wins (walk M-1..0, first with AccountSubTag)
    /// - 0x06 SelfDestruct: iterate 0..M-1, apply TryAdd semantics
    /// </summary>
    private static void NWayMergePerAddressHsst<TWriter, TReader, TPin>(
        HsstEnumerator[] outerEnums, int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        ref TWriter writer, BloomFilter? bloom = null, ulong addrBloomKey = 0) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // Get per-address HSST bounds (absolute offset from snapshot start) for each matching source
        using ArrayPoolList<(long Offset, long Length)> perAddrBoundsList = new(matchCount, matchCount);
        (long Offset, long Length)[] perAddrBounds = perAddrBoundsList.UnsafeGetInternalArray();
        for (int j = 0; j < matchCount; j++)
        {
            int srcIdx = matchingSources[j];
            // CurrentValue.Offset is snapshot-absolute (the enumerator was scoped to the column
            // within the whole snapshot), so it can be stored directly.
            Bound vb = outerEnums[srcIdx].CurrentValue;
            perAddrBounds[j] = (vb.Offset, vb.Length);
        }

        // perAddrBuilder is passed to several helpers by ref, so it can't be a `using`
        // declaration (the compiler refuses ref to using-variables). Manage its disposal
        // with a try/finally instead.
        HsstDenseByteIndexBuilder<TWriter> perAddrBuilder = new(ref writer);
        try
        {

        // Sub-tags 0x01 / 0x02 / 0x03: storage trie top / compact / fallback. Each source
        // carries an inner HSST keyed by encoded TreePath; values are NodeRefs (since
        // NWayMerge converts Full→Linked first). N-way streaming merge per sub-tag with
        // newest-wins on key collision; no destruct barrier since orphan nodes are
        // unreachable from the new storage root.
        MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, sessions, perAddrBounds,
            ref perAddrBuilder, PersistedSnapshot.StorageTopSubTag, innerKeySize: 3);
        MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, sessions, perAddrBounds,
            ref perAddrBuilder, PersistedSnapshot.StorageCompactSubTag, innerKeySize: 8);
        MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, sessions, perAddrBounds,
            ref perAddrBuilder, PersistedSnapshot.StorageFallbackSubTag, innerKeySize: 33);

        // Find newest destruct barrier: newest j where SelfDestructSubTag is present and
        // marks "destructed" ([0x00]). With DenseByteIndex per-address encoding, sub-tag
        // values are presence-marked: length 0 = absent, [0x00] = destructed, [0x01] = new.
        int destructBarrier = -1;
        for (int j = 0; j < matchCount; j++)
        {
            WholeReadSessionReader r = sessions[matchingSources[j]].GetReader();
            HsstReader<WholeReadSessionReader, NoOpPin> sd = new(in r, new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length));
            if (!sd.TrySeek(PersistedSnapshot.SelfDestructSubTag, out Bound sdb) || sdb.Length != 1) continue;
            using NoOpPin sdPin = r.PinBuffer(sdb.Offset, 1);
            if (sdPin.Buffer[0] == 0x00)
                destructBarrier = j;
        }

        // Sub-tag 0x04: Slots
        // Merge slots only from max(0, destructBarrier)..matchCount-1
        int slotStart = Math.Max(0, destructBarrier);

        {
            // Collect sources that have slots in the range; opportunistically feed the
            // bloom filter from the same seek pass — bloom and slot-merge need the
            // exact same set of sources / sub-tag bounds, so a separate pass would
            // just duplicate the seek.
            int slotSourceCount = 0;
            int slotCapacity = matchCount - slotStart;
            using ArrayPoolList<int> slotSourcesList = new(slotCapacity, slotCapacity);
            using ArrayPoolList<(long Offset, long Length)> slotBoundsList = new(slotCapacity, slotCapacity);
            int[] slotSources = slotSourcesList.UnsafeGetInternalArray();
            (long Offset, long Length)[] slotBounds = slotBoundsList.UnsafeGetInternalArray();
            for (int j = slotStart; j < matchCount; j++)
            {
                WholeReadSessionReader r = sessions[matchingSources[j]].GetReader();
                HsstReader<WholeReadSessionReader, NoOpPin> slot = new(in r, new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length));
                if (slot.TrySeek(PersistedSnapshot.SlotSubTag, out Bound slotBound))
                {
                    slotSources[slotSourceCount] = j;
                    // slotBound is reader-absolute (snapshot-absolute) since the scope was relative to the snapshot.
                    slotBounds[slotSourceCount] = (slotBound.Offset, slotBound.Length);
                    slotSourceCount++;
                    if (bloom is not null)
                        AddSlotKeysToBloom<WholeReadSessionReader, NoOpPin>(in r, slotBound, addrBloomKey, bloom);
                }
            }

            if (slotSourceCount == 1)
            {
                WholeReadSessionReader r = sessions[matchingSources[slotSources[0]]].GetReader();
                using NoOpPin slotPin = r.PinBuffer(slotBounds[0].Offset, slotBounds[0].Length);
                perAddrBuilder.Add(PersistedSnapshot.SlotSubTag, slotPin.Buffer);
            }
            else if (slotSourceCount > 1)
            {
                // N-way nested streaming merge on slot prefix-level HSSTs
                using ArrayPoolList<HsstEnumerator> slotEnumsList = new(slotSourceCount, slotSourceCount);
                using ArrayPoolList<bool> slotHasMoreList = new(slotSourceCount, slotSourceCount);
                using ArrayPoolList<WholeReadSession> slotSessionsList = new(slotSourceCount, slotSourceCount);
                HsstEnumerator[] slotEnums = slotEnumsList.UnsafeGetInternalArray();
                bool[] slotHasMore = slotHasMoreList.UnsafeGetInternalArray();
                WholeReadSession[] slotSessions = slotSessionsList.UnsafeGetInternalArray();
                try
                {
                    for (int j = 0; j < slotSourceCount; j++)
                    {
                        slotSessions[j] = sessions[matchingSources[slotSources[j]]];
                        WholeReadSessionReader slotReader = slotSessions[j].GetReader();
                        slotEnums[j] = new HsstEnumerator(in slotReader, new Bound(slotBounds[j].Offset, slotBounds[j].Length));
                        slotHasMore[j] = slotEnums[j].MoveNext(in slotReader);
                    }

                    ref TWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                    NWayNestedStreamingMerge<TWriter, TReader, TPin>(
                        slotEnums, slotHasMore, slotSourceCount, slotSessions,
                        ref slotWriter,
                        outerMinSep: 4, innerByteTagMap: true);
                    perAddrBuilder.FinishValueWrite(PersistedSnapshot.SlotSubTag);
                }
                finally
                {
                    for (int j = 0; j < slotSourceCount; j++) slotEnums[j].Dispose();
                }
            }
        }

        // Sub-tag 0x05: Account — newest wins (walk M-1..0, first present (length>0)).
        {
            for (int j = matchCount - 1; j >= 0; j--)
            {
                WholeReadSessionReader r = sessions[matchingSources[j]].GetReader();
                HsstReader<WholeReadSessionReader, NoOpPin> acct = new(in r, new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length));
                if (!acct.TrySeek(PersistedSnapshot.AccountSubTag, out Bound ab) || ab.Length == 0) continue;
                using NoOpPin acctPin = r.PinBuffer(ab.Offset, ab.Length);
                perAddrBuilder.Add(PersistedSnapshot.AccountSubTag, acctPin.Buffer);
                break;
            }
        }

        // Sub-tag 0x06: SelfDestruct — iterate 0..M-1, apply TryAdd semantics. Presence
        // is signalled by length>0 ([0x00]=destructed, [0x01]=new); absent entries (gap-
        // filled length 0 under DenseByteIndex) are ignored. Track the winning bound
        // snapshot-absolute so we can re-pin at the end without holding a span across
        // iterations.
        {
            int sdSrcJ = -1;
            long sdValOff = 0;
            long sdValLen = 0;

            for (int j = 0; j < matchCount; j++)
            {
                WholeReadSessionReader r = sessions[matchingSources[j]].GetReader();
                HsstReader<WholeReadSessionReader, NoOpPin> sd = new(in r, new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length));
                if (!sd.TrySeek(PersistedSnapshot.SelfDestructSubTag, out Bound sdb) || sdb.Length == 0) continue;

                if (sdSrcJ < 0)
                {
                    sdSrcJ = j;
                    sdValOff = sdb.Offset;
                    sdValLen = sdb.Length;
                }
                else
                {
                    // TryAdd: newer=destructed ([0x00]) -> destructed wins; newer=new ([0x01]) -> keep older.
                    using NoOpPin firstBytePin = r.PinBuffer(sdb.Offset, 1);
                    if (firstBytePin.Buffer[0] == 0x00)
                    {
                        sdSrcJ = j;
                        sdValOff = sdb.Offset;
                        sdValLen = sdb.Length;
                    }
                }
            }

            if (sdSrcJ >= 0)
            {
                WholeReadSessionReader r = sessions[matchingSources[sdSrcJ]].GetReader();
                using NoOpPin sdPin = r.PinBuffer(sdValOff, sdValLen);
                perAddrBuilder.Add(PersistedSnapshot.SelfDestructSubTag, sdPin.Buffer);
            }
        }

        perAddrBuilder.Build();
        }
        finally
        {
            perAddrBuilder.Dispose();
        }
    }

    /// <summary>
    /// Merge a single storage-trie sub-tag (0x01 top, 0x02 compact, or 0x03 fallback) across the M
    /// matching per-address sources into <paramref name="perAddrBuilder"/>. Each source's
    /// sub-tag value is an inner HSST(BTree) keyed by encoded TreePath; values are
    /// NodeRefs (NWayMergeSnapshots converts every Full input to Linked first). When
    /// only one source has the sub-tag, copies its bytes verbatim. With multiple sources,
    /// runs an N-way streaming merge into a fixed-size <see cref="HsstPackedArrayBuilder{TWriter}"/>
    /// (innerKeySize → NodeRef.Size). Newest wins on key collision; storage trie nodes
    /// are content-addressable so duplicate keys carry identical NodeRefs in practice.
    /// </summary>
    private static void MergeStorageTrieSubTag<TWriter, TReader, TPin>(
        int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        (long Offset, long Length)[] perAddrBounds,
        ref HsstDenseByteIndexBuilder<TWriter> perAddrBuilder,
        byte[] subTag,
        int innerKeySize) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using ArrayPoolList<int> srcsList = new(matchCount, matchCount);
        using ArrayPoolList<(long Offset, long Length)> boundsList = new(matchCount, matchCount);
        int[] srcs = srcsList.UnsafeGetInternalArray();
        (long Offset, long Length)[] subBounds = boundsList.UnsafeGetInternalArray();

        int active = 0;
        for (int j = 0; j < matchCount; j++)
        {
            WholeReadSessionReader r = sessions[matchingSources[j]].GetReader();
            HsstReader<WholeReadSessionReader, NoOpPin> sub = new(in r, new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length));
            if (sub.TrySeek(subTag, out Bound sb) && sb.Length > 0)
            {
                srcs[active] = j;
                subBounds[active] = (sb.Offset, sb.Length);
                active++;
            }
        }

        if (active == 0) return;

        if (active == 1)
        {
            int j = srcs[0];
            WholeReadSessionReader r = sessions[matchingSources[j]].GetReader();
            using NoOpPin pin = r.PinBuffer(subBounds[0].Offset, subBounds[0].Length);
            perAddrBuilder.Add(subTag, pin.Buffer);
            return;
        }

        // Multi-source: streaming N-way merge into a PackedArray. Cross-source min
        // selection and the bytes handed to Add both go through CopyCurrentLogicalKey,
        // which returns lex/BE bytes regardless of the source PackedArray's storage
        // layout (BE-stored or auto-LE-stored at innerKeySize ∈ {2,4,8}).
        using ArrayPoolList<HsstEnumerator> innerEnumsList = new(active, active);
        using ArrayPoolList<bool> innerHasMoreList = new(active, active);
        HsstEnumerator[] innerEnums = innerEnumsList.UnsafeGetInternalArray();
        bool[] innerHasMore = innerHasMoreList.UnsafeGetInternalArray();

        try
        {
            for (int j = 0; j < active; j++)
            {
                WholeReadSessionReader r = sessions[matchingSources[srcs[j]]].GetReader();
                innerEnums[j] = new HsstEnumerator(in r, new Bound(subBounds[j].Offset, subBounds[j].Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
            }

            ref TWriter subWriter = ref perAddrBuilder.BeginValueWrite();
            using HsstPackedArrayBuilder<TWriter> innerBuilder = new(ref subWriter, innerKeySize, NodeRef.Size);

            Span<byte> jKeyLogical = stackalloc byte[innerKeySize];
            Span<byte> mKeyLogical = stackalloc byte[innerKeySize];
            Span<byte> minKeyLogical = stackalloc byte[innerKeySize];

            while (true)
            {
                int minIdx = -1;
                for (int j = 0; j < active; j++)
                {
                    if (!innerHasMore[j]) continue;
                    if (minIdx < 0) { minIdx = j; continue; }
                    WholeReadSessionReader rJ = sessions[matchingSources[srcs[j]]].GetReader();
                    WholeReadSessionReader rM = sessions[matchingSources[srcs[minIdx]]].GetReader();
                    ReadOnlySpan<byte> kJ = innerEnums[j].CopyCurrentLogicalKey(in rJ, jKeyLogical);
                    ReadOnlySpan<byte> kM = innerEnums[minIdx].CopyCurrentLogicalKey(in rM, mKeyLogical);
                    int cmp = kJ.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = j;
                    else if (cmp == 0) minIdx = j; // newer (higher j) wins
                }
                if (minIdx < 0) break;

                Bound vb = innerEnums[minIdx].CurrentValue;
                WholeReadSessionReader rMin = sessions[matchingSources[srcs[minIdx]]].GetReader();
                using NoOpPin valPin = rMin.PinBuffer(vb.Offset, vb.Length);
                ReadOnlySpan<byte> minKey = innerEnums[minIdx].CopyCurrentLogicalKey(in rMin, minKeyLogical);
                innerBuilder.Add(minKey, valPin.Buffer);

                for (int j = 0; j < active; j++)
                {
                    if (j == minIdx || !innerHasMore[j]) continue;
                    WholeReadSessionReader rJ = sessions[matchingSources[srcs[j]]].GetReader();
                    ReadOnlySpan<byte> kJ = innerEnums[j].CopyCurrentLogicalKey(in rJ, jKeyLogical);
                    if (kJ.SequenceCompareTo(minKey) == 0)
                        innerHasMore[j] = innerEnums[j].MoveNext(in rJ);
                }
                {
                    WholeReadSessionReader r = sessions[matchingSources[srcs[minIdx]]].GetReader();
                    innerHasMore[minIdx] = innerEnums[minIdx].MoveNext(in r);
                }
            }

            innerBuilder.Build();
            perAddrBuilder.FinishValueWrite(subTag);
        }
        finally
        {
            for (int j = 0; j < active; j++) innerEnums[j].Dispose();
        }
    }

    /// <summary>
    /// N-way metadata merge: from_block/from_hash from oldest, to_block/to_hash/version from newest.
    /// Injects noderefs=[0x01] and ref_ids from referencedIds set.
    /// Emits in sorted key order.
    /// </summary>
    internal static void NWayMetadataMerge<TWriter, TReader, TPin>(
        PersistedSnapshotList snapshots, ref TWriter writer, HashSet<int> refIds) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = snapshots.Count;
        using WholeReadSession oldestSession = snapshots[0].BeginWholeReadSession();
        using WholeReadSession newestSession = snapshots[n - 1].BeginWholeReadSession();
        WholeReadSessionReader oldestReader = oldestSession.GetReader();
        WholeReadSessionReader newestReader = newestSession.GetReader();

        // Walk metadata fields directly through the long-aware readers. Each field
        // gets a narrow PinBuffer so the resulting Span is just the field bytes —
        // no wide pin of the entire metadata blob.
        HsstReader<WholeReadSessionReader, NoOpPin> oldestRoot = new(in oldestReader, new Bound(0, oldestReader.Length));
        oldestRoot.TrySeek(PersistedSnapshot.MetadataTag, out Bound oldestMetaScope);
        HsstReader<WholeReadSessionReader, NoOpPin> newestRoot = new(in newestReader, new Bound(0, newestReader.Length));
        newestRoot.TrySeek(PersistedSnapshot.MetadataTag, out Bound newestMetaScope);

        Bound fb = SeekField(in oldestReader, oldestMetaScope, "from_block"u8);
        Bound fh = SeekField(in oldestReader, oldestMetaScope, "from_hash"u8);
        Bound tb = SeekField(in newestReader, newestMetaScope, "to_block"u8);
        Bound th = SeekField(in newestReader, newestMetaScope, "to_hash"u8);
        Bound vb = SeekField(in newestReader, newestMetaScope, "version"u8);

        using NoOpPin fbPin = oldestReader.PinBuffer(fb.Offset, fb.Length);
        using NoOpPin fhPin = oldestReader.PinBuffer(fh.Offset, fh.Length);
        using NoOpPin tbPin = newestReader.PinBuffer(tb.Offset, tb.Length);
        using NoOpPin thPin = newestReader.PinBuffer(th.Offset, th.Length);
        using NoOpPin vPin = newestReader.PinBuffer(vb.Offset, vb.Length);

        static Bound SeekField(scoped in WholeReadSessionReader r, Bound scope, scoped ReadOnlySpan<byte> key)
        {
            HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, scope);
            hsst.TrySeek(key, out Bound matched);
            return matched;
        }
        ReadOnlySpan<byte> fromBlock = fbPin.Buffer;
        ReadOnlySpan<byte> fromHash = fhPin.Buffer;
        ReadOnlySpan<byte> toBlock = tbPin.Buffer;
        ReadOnlySpan<byte> toHash = thPin.Buffer;
        ReadOnlySpan<byte> version = vPin.Buffer;

        // Build ref_ids value
        byte[] refIdsValue = new byte[refIds.Count * 4];
        int idx = 0;
        foreach (int id in refIds)
        {
            BitConverter.TryWriteBytes(refIdsValue.AsSpan(idx * 4, 4), id);
            idx++;
        }

        using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer);

        // Emit all keys in sorted ASCII order:
        // "from_block" < "from_hash" < "noderefs" < "ref_ids" < "to_block" < "to_hash" < "version"
        builder.Add("from_block"u8, fromBlock);
        builder.Add("from_hash"u8, fromHash);
        builder.Add("noderefs"u8, [0x01]);
        builder.Add("ref_ids"u8, refIdsValue);
        builder.Add("to_block"u8, toBlock);
        builder.Add("to_hash"u8, toHash);
        builder.Add("version"u8, version);

        builder.Build();
    }

    private static void AddSlotKeysToBloom<TReader, TPin>(
        scoped in TReader reader, Bound slotScope, ulong addrKey, BloomFilter bloom)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        // slotScope addresses a 2-level HSST inside reader: prefix(31 bytes) → inner ByteTagMap(suffix(1 byte) → slot value).
        // We walk it through the source reader using long-aware Bounds, so it's safe even when
        // the section sits past the 2 GiB single-Span ceiling of the underlying file.
        Span<byte> fullSlot = stackalloc byte[32];
        HsstEnumerator<TReader, TPin> outerEnum = new(in reader, slotScope);
        while (outerEnum.MoveNext(in reader))
        {
            // Outer prefix is 31 bytes, inner suffix is 1 byte — together they fill fullSlot.
            outerEnum.CopyCurrentLogicalKey(in reader, fullSlot[..31]);
            Bound ovb = outerEnum.CurrentValue;
            HsstEnumerator<TReader, TPin> innerEnum = new(in reader, ovb);
            while (innerEnum.MoveNext(in reader))
            {
                innerEnum.CopyCurrentLogicalKey(in reader, fullSlot[31..]);
                ulong s0 = MemoryMarshal.Read<ulong>(fullSlot);
                ulong s1 = MemoryMarshal.Read<ulong>(fullSlot[8..]);
                ulong s2 = MemoryMarshal.Read<ulong>(fullSlot[16..]);
                ulong s3 = MemoryMarshal.Read<ulong>(fullSlot[24..]);
                bloom.Add(addrKey ^ s0 ^ s1 ^ s2 ^ s3);
            }
            innerEnum.Dispose();
        }
        outerEnum.Dispose();
    }
}
