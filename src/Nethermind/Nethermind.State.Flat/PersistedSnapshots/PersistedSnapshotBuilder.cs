// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
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
/// Builds columnar HSST byte data from an in-memory <see cref="Snapshot"/>. All
/// persisted snapshots are blob-backed: trie-node RLP values are stored as
/// <see cref="NodeRef"/>s pointing into blob arenas, while account / slot /
/// self-destruct values are inlined in the metadata HSST.
///
/// The outer HSST has 5 column entries, each containing an inner HSST. Inner HSST
/// keys are the entity keys without the tag prefix.
/// </summary>
public static class PersistedSnapshotBuilder
{
    private const int TopPathThreshold = 7;
    private const int CompactPathThreshold = 15;
    private const int StorageHashPrefixLength = 20;

    // Outer HSST column tags in iteration order, used by NWayMergeSnapshots.
    // Storage-trie data lives inside the per-address column 0x01 as sub-tags, so
    // 0x07/0x08 are gone from the on-disk layout.
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

    // Cached raw view fields for an open WholeReadSession. Used by the N-way merge helpers
    // to amortise the per-call ObjectDisposedException check + interface-dispatch cost of
    // WholeReadSession.GetReader over the entire merge loop. Callers populate one entry per
    // source at merge setup; the underlying session must outlive every call to Reader.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static WholeReadSessionReader Reader((IntPtr Ptr, long Len) v)
    {
        unsafe { return new WholeReadSessionReader((byte*)v.Ptr, v.Len); }
    }

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

    private static void WriteMetadataColumn<TWriter, TReader, TPin>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot, ushort blobArenaId) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // Metadata keys must be in sorted ASCII order:
        // "from_block" < "from_hash" < "ref_ids" < "to_block" < "to_hash" < "version"
        // ref_ids carries this snapshot's referenced blob arena id(s). For a freshly built
        // base snapshot it's a single int — the id of the blob arena the builder just wrote
        // its trie RLPs into. Compactor's NWayMetadataMerge replaces this with the union
        // of input snapshots' referenced ids.
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilder<TWriter, TReader, TPin> inner = new(ref innerWriter, PersistedSnapshot.MetadataKeyLength, expectedKeyCount: 6);

        Span<byte> blockNumBytes = stackalloc byte[8];
        Span<byte> refIdsBytes = stackalloc byte[2];

        BitConverter.TryWriteBytes(blockNumBytes, snapshot.From.BlockNumber);
        inner.Add(PersistedSnapshot.MetadataFromBlockKey, blockNumBytes);

        inner.Add(PersistedSnapshot.MetadataFromHashKey, snapshot.From.StateRoot.Bytes);

        BinaryPrimitives.WriteUInt16LittleEndian(refIdsBytes, blobArenaId);
        inner.Add(PersistedSnapshot.MetadataRefIdsKey, refIdsBytes);

        BitConverter.TryWriteBytes(blockNumBytes, snapshot.To.BlockNumber);
        inner.Add(PersistedSnapshot.MetadataToBlockKey, blockNumBytes);

        inner.Add(PersistedSnapshot.MetadataToHashKey, snapshot.To.StateRoot.Bytes);

        inner.Add(PersistedSnapshot.MetadataVersionKey, [0x01]);

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
        const int slotPrefixLength = 30;
        const int slotSuffixLength = 32 - slotPrefixLength;

        // Address-level HSST keyed by 20-byte address-hash prefix.
        ref TWriter addressWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilder<TWriter, TReader, TPin> addressLevel = new(ref addressWriter, StorageHashPrefixLength, new HsstBTreeOptions
        {
            MinSeparatorLength = 4,
        }, expectedKeyCount: uniqueAddressHashes.Count);
        // Slim-account RLP for any single account fits comfortably in 256 bytes (4×u256 fields
        // plus framing). Pool the scratch so it doesn't allocate per WriteAccountColumn call.
        byte[] rlpBuffer = ArrayPool<byte>.Shared.Rent(256);
        RlpStream rlpStream = new(rlpBuffer);
        Span<byte> slotKey = stackalloc byte[32];
        Span<byte> currentPrefixBuf = stackalloc byte[slotPrefixLength];
        Span<byte> topPathKey = stackalloc byte[4];
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
            //   0x01 storage top: nested HSST(4-byte path → RLP)
            //   0x02 storage compact: nested HSST(8-byte path → RLP)
            //   0x03 storage fallback: nested HSST(33-byte path → RLP)
            //   0x04 slots: nested HSST(SlotPrefix(30) → nested HSST(SlotSuffix(2) → bytes))
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
                using HsstBTreeBuilder<TWriter, TReader, TPin> topLevel = new(ref topWriter, keyLength: 4, new HsstBTreeOptions { MinSeparatorLength = 4 },
                    expectedKeyCount: storTopIdx - topStart);
                for (int i = topStart; i < storTopIdx; i++)
                {
                    (ValueHash256 _, TreePath path) = storTop[i];
                    snapshot.TryGetStorageNode((addrRefForStorageNode, path), out TrieNode? node);
                    path.EncodeWith4Byte(topPathKey);
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
                using HsstBTreeBuilder<TWriter, TReader, TPin> compactLevel = new(ref compactWriter, keyLength: 8, new HsstBTreeOptions { MinSeparatorLength = 8 },
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
                using HsstBTreeBuilder<TWriter, TReader, TPin> fbLevel = new(ref fbWriter, keyLength: 33, expectedKeyCount: storFallbackIdx - fallbackStart);
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
                using HsstBTreeBuilder<TWriter, TReader, TPin> prefixLevel = new(ref slotWriter, slotPrefixLength, new HsstBTreeOptions { MinSeparatorLength = 4 });

                while (storageIdx < sortedStorages.Count &&
                    sortedStorages[storageIdx].Key.AddrHash.Equals(addressHash))
                {
                    sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                    slotKey[..slotPrefixLength].CopyTo(currentPrefixBuf);
                    ReadOnlySpan<byte> currentPrefix = currentPrefixBuf;

                    ref TWriter suffixWriter = ref prefixLevel.BeginValueWrite();
                    using HsstBTreeBuilder<TWriter, TReader, TPin> suffixLevel = new(ref suffixWriter, keyLength: slotSuffixLength,
                        new HsstBTreeOptions { MinSeparatorLength = slotSuffixLength });

                    while (storageIdx < sortedStorages.Count &&
                        sortedStorages[storageIdx].Key.AddrHash.Equals(addressHash))
                    {
                        sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                        if (!slotKey[..slotPrefixLength].SequenceEqual(currentPrefix))
                            break;

                        SlotValue? value = sortedStorages[storageIdx].Value;
                        ReadOnlySpan<byte> suffixKey = slotKey.Slice(slotPrefixLength, slotSuffixLength);
                        if (value.HasValue)
                        {
                            ReadOnlySpan<byte> withoutLeadingZeros = value.Value.AsReadOnlySpan.WithoutLeadingZeros();
                            suffixLevel.Add(suffixKey, withoutLeadingZeros);
                        }
                        else
                        {
                            suffixLevel.Add(suffixKey, []);
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
        ArrayPool<byte>.Shared.Return(rlpBuffer);
    }

    private static void WriteStateTopNodesColumn<TWriter, TReader, TPin>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot, NativeMemoryList<TreePath> stateNodeKeys, BlobArenaWriter blobWriter, BloomFilter? trieBloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilder<TWriter, TReader, TPin> inner = new(ref innerWriter, keyLength: 4, new HsstBTreeOptions
        {
            MinSeparatorLength = 4,
        }, expectedKeyCount: stateNodeKeys.Count);
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
            trieBloom?.Add(PersistedSnapshotBloomBuilder.StatePathKey(in path));
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshot.StateTopNodesTag);
    }

    private static void WriteStateNodesColumnCompact<TWriter, TReader, TPin>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot, NativeMemoryList<TreePath> stateNodeKeys, BlobArenaWriter blobWriter, BloomFilter? trieBloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilder<TWriter, TReader, TPin> inner = new(ref innerWriter, keyLength: 8, new HsstBTreeOptions
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
        using HsstBTreeBuilder<TWriter, TReader, TPin> inner = new(ref innerWriter, keyLength: 33, expectedKeyCount: stateNodeKeys.Count);
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
    /// N-way merge of N persisted snapshots (oldest-first) into output buffer.
    /// Pre-converts all Full snapshots to Linked so the merge only handles Linked snapshots
    /// (all trie values are already NodeRefs). This eliminates the dual code path in trie merges.
    /// </summary>
    internal static void NWayMergeSnapshots<TWriter, TReader, TPin>(PersistedSnapshotList snapshots, ref TWriter writer, SortedSet<ushort> referencedBlobArenaIds, BloomFilter? bloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // Open one WholeReadSession per source for the whole merge — every column helper
        // reads through these without re-opening per-helper sessions (which would mmap +
        // MADV_NORMAL on open and MADV_DONTNEED on close between columns, dropping pages
        // we'd then re-fault for the next column). One open per source, one close at the
        // end, regardless of how many columns we walk.
        int n = snapshots.Count;
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        using NativeMemoryList<(IntPtr Ptr, long Len)> viewsList = new(n, n);
        WholeReadSession[] sessions = sessionsList.UnsafeGetInternalArray();
        Span<(IntPtr Ptr, long Len)> views = viewsList.AsSpan();
        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                views[i] = sessions[i].GetRawView();
            }

            NWayMergeSnapshotsWithViews<TWriter, TReader, TPin>(views, ref writer, referencedBlobArenaIds, bloom);
        }
        finally
        {
            for (int i = 0; i < n; i++) sessions[i]?.Dispose();
        }
    }

    /// <summary>
    /// Variant of <see cref="NWayMergeSnapshots"/> that takes pre-opened mmap views instead
    /// of opening (and closing) one <see cref="WholeReadSession"/> per source. Used by the
    /// compactor, which opens the sessions once at the top of <c>CompactRange</c> so the
    /// ref-ids read and the merge share the same mmap views.
    /// </summary>
    internal static void NWayMergeSnapshotsWithViews<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, ref TWriter writer,
        SortedSet<ushort> referencedBlobArenaIds, BloomFilter? bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
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
                    NWayMetadataMerge<TWriter, TReader, TPin>(views, ref valueWriter, referencedBlobArenaIds);
                    break;
                case 0x01:
                    NWayMergeAccountColumn<TWriter, TReader, TPin>(views, tag, ref valueWriter, bloom);
                    break;
                case 0x03:
                    NWayStreamingMerge<TWriter, TReader, TPin>(views, tag, ref valueWriter, keySize: 8);
                    break;
                case 0x05:
                    NWayStreamingMerge<TWriter, TReader, TPin>(views, tag, ref valueWriter, keySize: 4);
                    break;
                case 0x06:
                    NWayStreamingMerge<TWriter, TReader, TPin>(views, tag, ref valueWriter, keySize: 33);
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
    /// The caller supplies a parallel <paramref name="views"/> span — one entry per source —
    /// so the helper does not re-open per-reservation mmap views inside its scope.
    /// </summary>
    internal static void NWayStreamingMerge<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, byte[] tag, ref TWriter writer,
        int keySize) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        using ArrayPoolList<HsstEnumerator> enums = new(n, n);
        using NativeMemoryList<bool> hasMore = new(n, n);
        // Cache each source's current logical key once per MoveNext so the O(N) find-min
        // and match-detection scans don't redo CopyCurrentLogicalKey 2-3x per output key.
        // Slot i occupies keyBuf[i*keySize .. (i+1)*keySize].
        int keyStride = Math.Max(1, keySize);
        using NativeMemoryList<byte> keyBufList = new(n * keyStride, n * keyStride);
        Span<byte> keyBuf = keyBufList.AsSpan();

        try
        {
            for (int i = 0; i < n; i++)
            {
                WholeReadSessionReader r = Reader(views[i]);
                HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
                (long Offset, long Length) cb = hsst.TrySeek(tag, out Bound cbOut) ? (cbOut.Offset, cbOut.Length) : (0, 0);
                enums[i] = new HsstEnumerator(in r, new Bound(cb.Offset, cb.Length));
                hasMore[i] = enums[i].MoveNext(in r);
                if (hasMore[i])
                    enums[i].CopyCurrentLogicalKey(in r, keyBuf.Slice(i * keyStride, keyStride));
            }

            using HsstPackedArrayBuilder<TWriter> builder = new(ref writer, keySize, NodeRef.Size);

            while (true)
            {
                // Find min key across all active enumerators, newest wins on tie. Compares
                // operate on cached key slices — no re-copy per comparison.
                int minIdx = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    if (minIdx < 0)
                    {
                        minIdx = i;
                        continue;
                    }
                    ReadOnlySpan<byte> kI = keyBuf.Slice(i * keyStride, keyStride);
                    ReadOnlySpan<byte> kM = keyBuf.Slice(minIdx * keyStride, keyStride);
                    int cmp = kI.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = i;
                    else if (cmp == 0) minIdx = i; // newer (higher index) wins
                }

                if (minIdx < 0) break;

                ReadOnlySpan<byte> minKey = keyBuf.Slice(minIdx * keyStride, keyStride);
                Bound valBound = enums[minIdx].CurrentValue;
                WholeReadSessionReader minIdxReader = Reader(views[minIdx]);
                using NoOpPin valPin = minIdxReader.PinBuffer(valBound.Offset, valBound.Length);
                builder.Add(minKey, valPin.Buffer);

                for (int i = 0; i < n; i++)
                {
                    if (i == minIdx || !hasMore[i]) continue;
                    ReadOnlySpan<byte> kI = keyBuf.Slice(i * keyStride, keyStride);
                    if (kI.SequenceCompareTo(minKey) == 0)
                    {
                        WholeReadSessionReader rI = Reader(views[i]);
                        hasMore[i] = enums[i].MoveNext(in rI);
                        if (hasMore[i])
                            enums[i].CopyCurrentLogicalKey(in rI, keyBuf.Slice(i * keyStride, keyStride));
                    }
                }
                {
                    WholeReadSessionReader r = Reader(views[minIdx]);
                    hasMore[minIdx] = enums[minIdx].MoveNext(in r);
                    if (hasMore[minIdx])
                        enums[minIdx].CopyCurrentLogicalKey(in r, keyBuf.Slice(minIdx * keyStride, keyStride));
                }
            }

            builder.Build();
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i].Dispose();
        }
    }

    /// <summary>
    /// N-way nested streaming merge: outer keys merged across N sources,
    /// when M sources share an outer key their inner HSST values are merged via NWayStreamingMerge.
    /// Single-source keys are copied as-is.
    /// </summary>
    internal static void NWayNestedStreamingMerge<TWriter, TReader, TPin>(
        HsstEnumerator[] enums, Span<bool> hasMore, int n,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ref TWriter writer,
        int outerKeyLength, int innerKeyLength,
        int outerMinSep = 0, int innerMinSep = 0) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, outerKeyLength, new HsstBTreeOptions { MinSeparatorLength = outerMinSep });

        // Temp list for collecting matching source indices
        using NativeMemoryList<int> matchingSourcesList = new(n, n);
        Span<int> matchingSources = matchingSourcesList.AsSpan();

        // Cache each source's current outer key once per MoveNext. 64 covers every key
        // size that ends up in this merge: storage-hash address prefixes (≤32) and storage
        // path prefixes for the BTree variants (≤33). Slot i occupies keyBuf[i*64 .. ).
        const int KeyStride = 64;
        Span<byte> keyBuf = stackalloc byte[n * KeyStride];
        for (int i = 0; i < n; i++)
        {
            if (!hasMore[i]) continue;
            WholeReadSessionReader r = Reader(views[i]);
            enums[i].CopyCurrentLogicalKey(in r, keyBuf.Slice(i * KeyStride, outerKeyLength));
        }

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
                ReadOnlySpan<byte> kI = keyBuf.Slice(i * KeyStride, outerKeyLength);
                ReadOnlySpan<byte> kM = keyBuf.Slice(minIdx * KeyStride, outerKeyLength);
                int cmp = kI.SequenceCompareTo(kM);
                if (cmp < 0) minIdx = i;
            }

            if (minIdx < 0) break;

            ReadOnlySpan<byte> minKey = keyBuf.Slice(minIdx * KeyStride, outerKeyLength);

            // Collect all sources with this key
            int matchCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (!hasMore[i]) continue;
                ReadOnlySpan<byte> kI = keyBuf.Slice(i * KeyStride, outerKeyLength);
                if (kI.SequenceCompareTo(minKey) == 0)
                    matchingSources[matchCount++] = i;
            }

            if (matchCount == 1)
            {
                // Single source: copy as-is
                int srcIdx = matchingSources[0];
                Bound vb = enums[srcIdx].CurrentValue;
                WholeReadSessionReader srcReader = Reader(views[srcIdx]);
                using NoOpPin valPin = srcReader.PinBuffer(vb.Offset, vb.Length);
                builder.Add(minKey, valPin.Buffer);
            }
            else
            {
                // M sources: create M inner enumerators and merge
                ref TWriter innerWriter = ref builder.BeginValueWrite();
                NWayInnerMerge<TWriter, TReader, TPin>(enums, matchingSources, matchCount, views,
                    ref innerWriter, innerKeyLength, innerMinSep);
                builder.FinishValueWrite(minKey);
            }

            // Advance all matching, refilling cached outer keys.
            for (int j = 0; j < matchCount; j++)
            {
                int i = matchingSources[j];
                WholeReadSessionReader r = Reader(views[i]);
                hasMore[i] = enums[i].MoveNext(in r);
                if (hasMore[i])
                    enums[i].CopyCurrentLogicalKey(in r, keyBuf.Slice(i * KeyStride, outerKeyLength));
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
        HsstEnumerator[] outerEnums, ReadOnlySpan<int> matchingSources, int matchCount,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ref TWriter writer,
        int innerKeyLength,
        int minSeparatorLength = 0) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using ArrayPoolList<HsstEnumerator> innerEnums = new(matchCount, matchCount);
        using NativeMemoryList<bool> innerHasMore = new(matchCount, matchCount);
        // Cache each inner enumerator's current key once per MoveNext. innerKeyLength ≤ 33
        // for any caller; 64 stride covers comfortably with room for future growth.
        const int KeyStride = 64;
        Span<byte> innerKeyBuf = stackalloc byte[matchCount * KeyStride];

        try
        {
            for (int j = 0; j < matchCount; j++)
            {
                int srcIdx = matchingSources[j];
                Bound vb = outerEnums[srcIdx].CurrentValue;
                WholeReadSessionReader r = Reader(views[srcIdx]);
                innerEnums[j] = new HsstEnumerator(in r, new Bound(vb.Offset, vb.Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
                if (innerHasMore[j])
                    innerEnums[j].CopyCurrentLogicalKey(in r, innerKeyBuf.Slice(j * KeyStride, innerKeyLength));
            }

            using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, innerKeyLength, new HsstBTreeOptions { MinSeparatorLength = minSeparatorLength });
            while (true)
            {
                int minIdx = -1;
                for (int j = 0; j < matchCount; j++)
                {
                    if (!innerHasMore[j]) continue;
                    if (minIdx < 0) { minIdx = j; continue; }
                    ReadOnlySpan<byte> kJ = innerKeyBuf.Slice(j * KeyStride, innerKeyLength);
                    ReadOnlySpan<byte> kM = innerKeyBuf.Slice(minIdx * KeyStride, innerKeyLength);
                    int cmp = kJ.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = j;
                    else if (cmp == 0) minIdx = j; // newer (higher j = higher source index) wins
                }
                if (minIdx < 0) break;

                Bound vb = innerEnums[minIdx].CurrentValue;
                WholeReadSessionReader rMin = Reader(views[matchingSources[minIdx]]);
                ReadOnlySpan<byte> minKey = innerKeyBuf.Slice(minIdx * KeyStride, innerKeyLength);
                using NoOpPin valPin = rMin.PinBuffer(vb.Offset, vb.Length);
                builder.Add(minKey, valPin.Buffer);

                for (int j = 0; j < matchCount; j++)
                {
                    if (j == minIdx || !innerHasMore[j]) continue;
                    ReadOnlySpan<byte> kJ = innerKeyBuf.Slice(j * KeyStride, innerKeyLength);
                    if (kJ.SequenceCompareTo(minKey) == 0)
                    {
                        WholeReadSessionReader rJ = Reader(views[matchingSources[j]]);
                        innerHasMore[j] = innerEnums[j].MoveNext(in rJ);
                        if (innerHasMore[j])
                            innerEnums[j].CopyCurrentLogicalKey(in rJ, innerKeyBuf.Slice(j * KeyStride, innerKeyLength));
                    }
                }
                {
                    WholeReadSessionReader r = Reader(views[matchingSources[minIdx]]);
                    innerHasMore[minIdx] = innerEnums[minIdx].MoveNext(in r);
                    if (innerHasMore[minIdx])
                        innerEnums[minIdx].CopyCurrentLogicalKey(in r, innerKeyBuf.Slice(minIdx * KeyStride, innerKeyLength));
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
    /// N-way nested streaming merge across N persisted snapshots.
    /// Initializes enumerators from snapshot data and delegates to the core merge method.
    /// </summary>
    internal static void NWayNestedStreamingMerge<TWriter, TReader, TPin>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer,
        int outerKeyLength, int innerKeyLength,
        int outerMinSep = 0, int innerMinSep = 0) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstEnumerator> enumsList = new(n, n);
        using NativeMemoryList<bool> hasMoreList = new(n, n);
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        using NativeMemoryList<(IntPtr Ptr, long Len)> viewsList = new(n, n);
        HsstEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        Span<bool> hasMore = hasMoreList.AsSpan();
        WholeReadSession[] sessions = sessionsList.UnsafeGetInternalArray();
        Span<(IntPtr Ptr, long Len)> views = viewsList.AsSpan();

        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                views[i] = sessions[i].GetRawView();
                WholeReadSessionReader r = Reader(views[i]);
                HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
                (long Offset, long Length) cb = hsst.TrySeek(tag, out Bound cbOut) ? (cbOut.Offset, cbOut.Length) : (0, 0);
                enums[i] = new HsstEnumerator(in r, new Bound(cb.Offset, cb.Length));
                hasMore[i] = enums[i].MoveNext(in r);
            }

            NWayNestedStreamingMerge<TWriter, TReader, TPin>(enums, hasMore, n, views,
                ref writer, outerKeyLength, innerKeyLength, outerMinSep, innerMinSep);
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
        int outerKeyLength, int outerMinSep, int innerKeySize) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstEnumerator> enumsList = new(n, n);
        using NativeMemoryList<bool> hasMoreList = new(n, n);
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        using NativeMemoryList<(IntPtr Ptr, long Len)> viewsList = new(n, n);
        using NativeMemoryList<int> matchingSourcesList = new(n, n);
        HsstEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        Span<bool> hasMore = hasMoreList.AsSpan();
        WholeReadSession[] sessions = sessionsList.UnsafeGetInternalArray();
        Span<(IntPtr Ptr, long Len)> views = viewsList.AsSpan();
        Span<int> matchingSources = matchingSourcesList.AsSpan();

        // Cache each source's current outer key once per MoveNext (outer keys ≤ 32 bytes).
        const int KeyStride = 64;
        Span<byte> keyBuf = stackalloc byte[n * KeyStride];

        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                views[i] = sessions[i].GetRawView();
                WholeReadSessionReader r = Reader(views[i]);
                HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
                (long Offset, long Length) cb = hsst.TrySeek(tag, out Bound cbOut) ? (cbOut.Offset, cbOut.Length) : (0, 0);
                enums[i] = new HsstEnumerator(in r, new Bound(cb.Offset, cb.Length));
                hasMore[i] = enums[i].MoveNext(in r);
                if (hasMore[i])
                    enums[i].CopyCurrentLogicalKey(in r, keyBuf.Slice(i * KeyStride, outerKeyLength));
            }

            using HsstBTreeBuilder<TWriter, TReader, TPin> outerBuilder = new(ref writer, outerKeyLength, new HsstBTreeOptions { MinSeparatorLength = outerMinSep });

            while (true)
            {
                int minIdx = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    if (minIdx < 0) { minIdx = i; continue; }
                    ReadOnlySpan<byte> kI = keyBuf.Slice(i * KeyStride, outerKeyLength);
                    ReadOnlySpan<byte> kM = keyBuf.Slice(minIdx * KeyStride, outerKeyLength);
                    int cmp = kI.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = i;
                }
                if (minIdx < 0) break;

                ReadOnlySpan<byte> minKey = keyBuf.Slice(minIdx * KeyStride, outerKeyLength);

                int matchCount = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    ReadOnlySpan<byte> kI = keyBuf.Slice(i * KeyStride, outerKeyLength);
                    if (kI.SequenceCompareTo(minKey) == 0)
                        matchingSources[matchCount++] = i;
                }

                if (matchCount == 1)
                {
                    int srcIdx = matchingSources[0];
                    Bound vb = enums[srcIdx].CurrentValue;
                    WholeReadSessionReader srcReader = Reader(views[srcIdx]);
                    using NoOpPin valPin = srcReader.PinBuffer(vb.Offset, vb.Length);
                    outerBuilder.Add(minKey, valPin.Buffer);
                }
                else
                {
                    ref TWriter innerWriter = ref outerBuilder.BeginValueWrite();
                    NWayInnerMergeTrie<TWriter, TReader, TPin>(enums, matchingSources, matchCount, views,
                        ref innerWriter, innerKeySize);
                    outerBuilder.FinishValueWrite(minKey);
                }

                for (int j = 0; j < matchCount; j++)
                {
                    int i = matchingSources[j];
                    WholeReadSessionReader r = Reader(views[i]);
                    hasMore[i] = enums[i].MoveNext(in r);
                    if (hasMore[i])
                        enums[i].CopyCurrentLogicalKey(in r, keyBuf.Slice(i * KeyStride, outerKeyLength));
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
        HsstEnumerator[] outerEnums, ReadOnlySpan<int> matchingSources, int matchCount,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ref TWriter writer,
        int keySize) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using ArrayPoolList<HsstEnumerator> innerEnums = new(matchCount, matchCount);
        using NativeMemoryList<bool> innerHasMore = new(matchCount, matchCount);
        // Cache each inner enumerator's current key (trie path, keySize ≤ 33).
        const int KeyStride = 64;
        Span<byte> keyBuf = stackalloc byte[matchCount * KeyStride];

        try
        {
            for (int j = 0; j < matchCount; j++)
            {
                int srcIdx = matchingSources[j];
                Bound vb = outerEnums[srcIdx].CurrentValue;
                WholeReadSessionReader r = Reader(views[srcIdx]);
                innerEnums[j] = new HsstEnumerator(in r, new Bound(vb.Offset, vb.Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
                if (innerHasMore[j])
                    innerEnums[j].CopyCurrentLogicalKey(in r, keyBuf.Slice(j * KeyStride, keySize));
            }

            using HsstPackedArrayBuilder<TWriter> builder = new(ref writer, keySize, NodeRef.Size);

            while (true)
            {
                int minIdx = -1;
                for (int j = 0; j < matchCount; j++)
                {
                    if (!innerHasMore[j]) continue;
                    if (minIdx < 0) { minIdx = j; continue; }
                    ReadOnlySpan<byte> kJ = keyBuf.Slice(j * KeyStride, keySize);
                    ReadOnlySpan<byte> kM = keyBuf.Slice(minIdx * KeyStride, keySize);
                    int cmp = kJ.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = j;
                    else if (cmp == 0) minIdx = j; // newer wins
                }
                if (minIdx < 0) break;

                Bound vb2 = innerEnums[minIdx].CurrentValue;
                WholeReadSessionReader minReader = Reader(views[matchingSources[minIdx]]);
                ReadOnlySpan<byte> minKey = keyBuf.Slice(minIdx * KeyStride, keySize);
                using NoOpPin valPin = minReader.PinBuffer(vb2.Offset, vb2.Length);
                builder.Add(minKey, valPin.Buffer);

                for (int j = 0; j < matchCount; j++)
                {
                    if (j == minIdx || !innerHasMore[j]) continue;
                    ReadOnlySpan<byte> kJ = keyBuf.Slice(j * KeyStride, keySize);
                    if (kJ.SequenceCompareTo(minKey) == 0)
                    {
                        WholeReadSessionReader jr = Reader(views[matchingSources[j]]);
                        innerHasMore[j] = innerEnums[j].MoveNext(in jr);
                        if (innerHasMore[j])
                            innerEnums[j].CopyCurrentLogicalKey(in jr, keyBuf.Slice(j * KeyStride, keySize));
                    }
                }
                {
                    WholeReadSessionReader mr = Reader(views[matchingSources[minIdx]]);
                    innerHasMore[minIdx] = innerEnums[minIdx].MoveNext(in mr);
                    if (innerHasMore[minIdx])
                        innerEnums[minIdx].CopyCurrentLogicalKey(in mr, keyBuf.Slice(minIdx * KeyStride, keySize));
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
    /// Outer: 20-byte address keys (minSep=4). Addresses with a single matching source
    /// byte-copy the per-address HSST blob verbatim (every internal pointer is
    /// HSST-relative, so a relocation stays readable); collisions go through
    /// <see cref="NWayMergePerAddressHsst"/>.
    /// </summary>
    internal static void NWayMergeAccountColumn<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, byte[] tag, ref TWriter writer, BloomFilter? bloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        using ArrayPoolList<HsstEnumerator> enumsList = new(n, n);
        using NativeMemoryList<bool> hasMoreList = new(n, n);
        using NativeMemoryList<int> matchingSourcesList = new(n, n);
        HsstEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        Span<bool> hasMore = hasMoreList.AsSpan();
        Span<int> matchingSources = matchingSourcesList.AsSpan();

        // Cache each source's current 20-byte address-hash key (stride 32 with room).
        const int KeyStride = 32;
        const int AddrKeyLen = StorageHashPrefixLength;
        Span<byte> keyBuf = stackalloc byte[n * KeyStride];

        try
        {
            for (int i = 0; i < n; i++)
            {
                WholeReadSessionReader r = Reader(views[i]);
                HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
                (long Offset, long Length) cb = hsst.TrySeek(tag, out Bound cbOut) ? (cbOut.Offset, cbOut.Length) : (0, 0);
                enums[i] = new HsstEnumerator(in r, new Bound(cb.Offset, cb.Length));
                hasMore[i] = enums[i].MoveNext(in r);
                if (hasMore[i])
                    enums[i].CopyCurrentLogicalKey(in r, keyBuf.Slice(i * KeyStride, AddrKeyLen));
            }

            using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, StorageHashPrefixLength, new HsstBTreeOptions { MinSeparatorLength = 4 });

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
                    ReadOnlySpan<byte> kI = keyBuf.Slice(i * KeyStride, AddrKeyLen);
                    ReadOnlySpan<byte> kM = keyBuf.Slice(minIdx * KeyStride, AddrKeyLen);
                    int cmp = kI.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = i;
                }

                if (minIdx < 0) break;

                ReadOnlySpan<byte> minKey = keyBuf.Slice(minIdx * KeyStride, AddrKeyLen);

                int matchCount = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    ReadOnlySpan<byte> kI = keyBuf.Slice(i * KeyStride, AddrKeyLen);
                    if (kI.SequenceCompareTo(minKey) == 0)
                        matchingSources[matchCount++] = i;
                }

                if (matchCount == 1)
                {
                    // Single-source fast path: byte-copy the source's per-address HSST blob.
                    // HSST internal pointers are HSST-relative (childOffset / dense-index ends
                    // are stored as deltas from the blob start), so a verbatim relocation to
                    // the destination writer position stays readable. The per-address sub-tags
                    // (account 0x05, self-destruct 0x06, slots 0x04, storage 0x01/0x02/0x03)
                    // ride along inside the copied blob — no per-sub-tag merge needed. Streamed
                    // via the long-aware IByteBufferWriter.Copy so blobs over the 2 GiB single-
                    // Span ceiling stay safe.
                    int srcIdx = matchingSources[0];
                    Bound vb = enums[srcIdx].CurrentValue;
                    WholeReadSessionReader srcReader = Reader(views[srcIdx]);
                    ref TWriter perAddrWriter = ref builder.BeginValueWrite();
                    IByteBufferWriter.Copy<TWriter, WholeReadSessionReader, NoOpPin>(ref perAddrWriter, in srcReader, vb);
                    builder.FinishValueWrite(minKey);
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
                    // M > 1 sources collide on this address: merge per-address HSSTs.
                    ref TWriter perAddrWriter = ref builder.BeginValueWrite();
                    ulong addrKey = 0;
                    if (bloom is not null)
                    {
                        addrKey = MemoryMarshal.Read<ulong>(minKey);
                        bloom.Add(addrKey);
                    }
                    NWayMergePerAddressHsst<TWriter, TReader, TPin>(
                        enums, matchingSources, matchCount, views,
                        ref perAddrWriter, bloom, addrKey);
                    builder.FinishValueWrite(minKey);
                }

                for (int j = 0; j < matchCount; j++)
                {
                    int i = matchingSources[j];
                    WholeReadSessionReader r = Reader(views[i]);
                    hasMore[i] = enums[i].MoveNext(in r);
                    if (hasMore[i])
                        enums[i].CopyCurrentLogicalKey(in r, keyBuf.Slice(i * KeyStride, AddrKeyLen));
                }
            }

            builder.Build();
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i].Dispose();
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
    // Per-address DenseByteIndex max tag + 1 (sub-tags 0x01..0x06 are populated). Allows
    // a single TryResolveAll per source to retrieve every sub-tag bound at once.
    private const int PerAddrSubTagCount = 7;

    private static void NWayMergePerAddressHsst<TWriter, TReader, TPin>(
        HsstEnumerator[] outerEnums, ReadOnlySpan<int> matchingSources, int matchCount,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ref TWriter writer, BloomFilter? bloom = null, ulong addrBloomKey = 0) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // Get per-address HSST bounds (absolute offset from snapshot start) for each matching source.
        using NativeMemoryList<(long Offset, long Length)> perAddrBoundsList = new(matchCount, matchCount);
        Span<(long Offset, long Length)> perAddrBounds = perAddrBoundsList.AsSpan();
        for (int j = 0; j < matchCount; j++)
        {
            int srcIdx = matchingSources[j];
            // CurrentValue.Offset is snapshot-absolute (the enumerator was scoped to the column
            // within the whole snapshot), so it can be stored directly.
            Bound vb = outerEnums[srcIdx].CurrentValue;
            perAddrBounds[j] = (vb.Offset, vb.Length);
        }

        // Resolve every sub-tag bound for every matching source in a single pass through
        // each source's DenseByteIndex. Replaces 6+ per-source TrySeek calls (each of which
        // re-read the trailer and re-pinned the ends array). Indexed as
        // subTagBounds[j * PerAddrSubTagCount + tag] for source j, sub-tag value `tag`.
        using NativeMemoryList<Bound> subTagBoundsList = new(matchCount * PerAddrSubTagCount, matchCount * PerAddrSubTagCount);
        Span<Bound> subTagBounds = subTagBoundsList.AsSpan();
        for (int j = 0; j < matchCount; j++)
        {
            WholeReadSessionReader r = Reader(views[matchingSources[j]]);
            HsstDenseByteIndexReader.TryResolveAll<WholeReadSessionReader, NoOpPin>(
                in r,
                new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length),
                subTagBounds.Slice(j * PerAddrSubTagCount, PerAddrSubTagCount));
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
            MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, views, subTagBounds,
                ref perAddrBuilder, PersistedSnapshot.StorageTopSubTag, subTagIdx: PersistedSnapshot.StorageTopSubTag[0], innerKeySize: 4);
            MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, views, subTagBounds,
                ref perAddrBuilder, PersistedSnapshot.StorageCompactSubTag, subTagIdx: PersistedSnapshot.StorageCompactSubTag[0], innerKeySize: 8);
            MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, views, subTagBounds,
                ref perAddrBuilder, PersistedSnapshot.StorageFallbackSubTag, subTagIdx: PersistedSnapshot.StorageFallbackSubTag[0], innerKeySize: 33);

            // Find newest destruct barrier: newest j where SelfDestructSubTag is present and
            // marks "destructed" ([0x00]). With DenseByteIndex per-address encoding, sub-tag
            // values are presence-marked: length 0 = absent, [0x00] = destructed, [0x01] = new.
            int sdTag = PersistedSnapshot.SelfDestructSubTag[0];
            int destructBarrier = -1;
            for (int j = 0; j < matchCount; j++)
            {
                Bound sdb = subTagBounds[j * PerAddrSubTagCount + sdTag];
                if (sdb.Length != 1) continue;
                WholeReadSessionReader r = Reader(views[matchingSources[j]]);
                using NoOpPin sdPin = r.PinBuffer(sdb.Offset, 1);
                if (sdPin.Buffer[0] == 0x00)
                    destructBarrier = j;
            }

            // Sub-tag 0x04: Slots
            // Merge slots only from max(0, destructBarrier)..matchCount-1. The slot merge
            // emits bloom adds inline from the merged stream (one walk per source) — the
            // separate pre-pass that did a duplicate walk per source has been removed.
            int slotStart = Math.Max(0, destructBarrier);
            int slotTag = PersistedSnapshot.SlotSubTag[0];

            {
                int slotSourceCount = 0;
                int slotCapacity = matchCount - slotStart;
                using NativeMemoryList<int> slotSourcesList = new(slotCapacity, slotCapacity);
                using NativeMemoryList<(long Offset, long Length)> slotBoundsList = new(slotCapacity, slotCapacity);
                Span<int> slotSources = slotSourcesList.AsSpan();
                Span<(long Offset, long Length)> slotBounds = slotBoundsList.AsSpan();
                for (int j = slotStart; j < matchCount; j++)
                {
                    Bound slotBound = subTagBounds[j * PerAddrSubTagCount + slotTag];
                    if (slotBound.Length > 0)
                    {
                        slotSources[slotSourceCount] = matchingSources[j];
                        slotBounds[slotSourceCount] = (slotBound.Offset, slotBound.Length);
                        slotSourceCount++;
                    }
                }

                if (slotSourceCount == 1)
                {
                    // Single-source fast path: byte-copy the source's slot HSST blob.
                    // HSST internal pointers are HSST-relative, so the relocated blob stays
                    // readable. Streamed via the long-aware IByteBufferWriter.Copy so a slot
                    // HSST above the 2 GiB single-Span ceiling stays safe. Bloom adds are
                    // walked separately since this path skips NWayInnerSlotMerge.
                    WholeReadSessionReader slotReader = Reader(views[slotSources[0]]);
                    Bound slotBlob = new(slotBounds[0].Offset, slotBounds[0].Length);
                    ref TWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                    IByteBufferWriter.Copy<TWriter, WholeReadSessionReader, NoOpPin>(ref slotWriter, in slotReader, slotBlob);
                    perAddrBuilder.FinishValueWrite(PersistedSnapshot.SlotSubTag);
                    if (bloom is not null)
                        AddSlotKeysToBloom<WholeReadSessionReader, NoOpPin>(in slotReader, slotBlob, addrBloomKey, bloom);
                }
                else if (slotSourceCount > 1)
                {
                    // M > 1 sources collide on this address's slots: streaming merge through
                    // NWayNestedStreamingSlotMerge / NWayInnerSlotMerge folds bloom adds in.
                    using ArrayPoolList<HsstEnumerator> slotEnumsList = new(slotSourceCount, slotSourceCount);
                    using NativeMemoryList<bool> slotHasMoreList = new(slotSourceCount, slotSourceCount);
                    using NativeMemoryList<(IntPtr Ptr, long Len)> slotViewsList = new(slotSourceCount, slotSourceCount);
                    HsstEnumerator[] slotEnums = slotEnumsList.UnsafeGetInternalArray();
                    Span<bool> slotHasMore = slotHasMoreList.AsSpan();
                    Span<(IntPtr Ptr, long Len)> slotViews = slotViewsList.AsSpan();
                    try
                    {
                        for (int j = 0; j < slotSourceCount; j++)
                        {
                            slotViews[j] = views[slotSources[j]];
                            WholeReadSessionReader slotReader = Reader(slotViews[j]);
                            slotEnums[j] = new HsstEnumerator(in slotReader, new Bound(slotBounds[j].Offset, slotBounds[j].Length));
                            slotHasMore[j] = slotEnums[j].MoveNext(in slotReader);
                        }

                        ref TWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                        NWayNestedStreamingSlotMerge<TWriter, TReader, TPin>(
                            slotEnums, slotHasMore, slotSourceCount, slotViews,
                            ref slotWriter, bloom, addrBloomKey);
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
                int acctTag = PersistedSnapshot.AccountSubTag[0];
                for (int j = matchCount - 1; j >= 0; j--)
                {
                    Bound ab = subTagBounds[j * PerAddrSubTagCount + acctTag];
                    if (ab.Length == 0) continue;
                    WholeReadSessionReader r = Reader(views[matchingSources[j]]);
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
                    Bound sdb = subTagBounds[j * PerAddrSubTagCount + sdTag];
                    if (sdb.Length == 0) continue;

                    if (sdSrcJ < 0)
                    {
                        sdSrcJ = j;
                        sdValOff = sdb.Offset;
                        sdValLen = sdb.Length;
                    }
                    else
                    {
                        // TryAdd: newer=destructed ([0x00]) -> destructed wins; newer=new ([0x01]) -> keep older.
                        WholeReadSessionReader r = Reader(views[matchingSources[j]]);
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
                    WholeReadSessionReader r = Reader(views[matchingSources[sdSrcJ]]);
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
        ReadOnlySpan<int> matchingSources, int matchCount,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ReadOnlySpan<Bound> subTagBounds,
        ref HsstDenseByteIndexBuilder<TWriter> perAddrBuilder,
        byte[] subTag,
        int subTagIdx,
        int innerKeySize) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using NativeMemoryList<int> srcsList = new(matchCount, matchCount);
        using NativeMemoryList<(long Offset, long Length)> boundsList = new(matchCount, matchCount);
        Span<int> srcs = srcsList.AsSpan();
        Span<(long Offset, long Length)> subBounds = boundsList.AsSpan();

        int active = 0;
        for (int j = 0; j < matchCount; j++)
        {
            Bound sb = subTagBounds[j * PerAddrSubTagCount + subTagIdx];
            if (sb.Length > 0)
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
            WholeReadSessionReader r = Reader(views[matchingSources[j]]);
            using NoOpPin pin = r.PinBuffer(subBounds[0].Offset, subBounds[0].Length);
            perAddrBuilder.Add(subTag, pin.Buffer);
            return;
        }

        // Multi-source: streaming N-way merge into a PackedArray with cached inner keys.
        // Cross-source min selection and the bytes handed to Add both go through
        // CopyCurrentLogicalKey, which returns lex/BE bytes regardless of the source
        // PackedArray's storage layout (BE-stored or auto-LE-stored at innerKeySize ∈ {2,4,8}).
        using ArrayPoolList<HsstEnumerator> innerEnumsList = new(active, active);
        using NativeMemoryList<bool> innerHasMoreList = new(active, active);
        HsstEnumerator[] innerEnums = innerEnumsList.UnsafeGetInternalArray();
        Span<bool> innerHasMore = innerHasMoreList.AsSpan();
        Span<byte> keyBuf = stackalloc byte[active * innerKeySize];

        try
        {
            for (int j = 0; j < active; j++)
            {
                WholeReadSessionReader r = Reader(views[matchingSources[srcs[j]]]);
                innerEnums[j] = new HsstEnumerator(in r, new Bound(subBounds[j].Offset, subBounds[j].Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
                if (innerHasMore[j])
                    innerEnums[j].CopyCurrentLogicalKey(in r, keyBuf.Slice(j * innerKeySize, innerKeySize));
            }

            ref TWriter subWriter = ref perAddrBuilder.BeginValueWrite();
            using HsstPackedArrayBuilder<TWriter> innerBuilder = new(ref subWriter, innerKeySize, NodeRef.Size);

            while (true)
            {
                int minIdx = -1;
                for (int j = 0; j < active; j++)
                {
                    if (!innerHasMore[j]) continue;
                    if (minIdx < 0) { minIdx = j; continue; }
                    ReadOnlySpan<byte> kJ = keyBuf.Slice(j * innerKeySize, innerKeySize);
                    ReadOnlySpan<byte> kM = keyBuf.Slice(minIdx * innerKeySize, innerKeySize);
                    int cmp = kJ.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = j;
                    else if (cmp == 0) minIdx = j; // newer (higher j) wins
                }
                if (minIdx < 0) break;

                Bound vb = innerEnums[minIdx].CurrentValue;
                WholeReadSessionReader rMin = Reader(views[matchingSources[srcs[minIdx]]]);
                ReadOnlySpan<byte> minKey = keyBuf.Slice(minIdx * innerKeySize, innerKeySize);
                using NoOpPin valPin = rMin.PinBuffer(vb.Offset, vb.Length);
                innerBuilder.Add(minKey, valPin.Buffer);

                for (int j = 0; j < active; j++)
                {
                    if (j == minIdx || !innerHasMore[j]) continue;
                    ReadOnlySpan<byte> kJ = keyBuf.Slice(j * innerKeySize, innerKeySize);
                    if (kJ.SequenceCompareTo(minKey) == 0)
                    {
                        WholeReadSessionReader rJ = Reader(views[matchingSources[srcs[j]]]);
                        innerHasMore[j] = innerEnums[j].MoveNext(in rJ);
                        if (innerHasMore[j])
                            innerEnums[j].CopyCurrentLogicalKey(in rJ, keyBuf.Slice(j * innerKeySize, innerKeySize));
                    }
                }
                {
                    WholeReadSessionReader r = Reader(views[matchingSources[srcs[minIdx]]]);
                    innerHasMore[minIdx] = innerEnums[minIdx].MoveNext(in r);
                    if (innerHasMore[minIdx])
                        innerEnums[minIdx].CopyCurrentLogicalKey(in r, keyBuf.Slice(minIdx * innerKeySize, innerKeySize));
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
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, ref TWriter writer, SortedSet<ushort> refIds) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        WholeReadSessionReader oldestReader = Reader(views[0]);
        WholeReadSessionReader newestReader = Reader(views[n - 1]);

        // Walk metadata fields directly through the long-aware readers. Each field
        // gets a narrow PinBuffer so the resulting Span is just the field bytes —
        // no wide pin of the entire metadata blob.
        HsstReader<WholeReadSessionReader, NoOpPin> oldestRoot = new(in oldestReader, new Bound(0, oldestReader.Length));
        oldestRoot.TrySeek(PersistedSnapshot.MetadataTag, out Bound oldestMetaScope);
        HsstReader<WholeReadSessionReader, NoOpPin> newestRoot = new(in newestReader, new Bound(0, newestReader.Length));
        newestRoot.TrySeek(PersistedSnapshot.MetadataTag, out Bound newestMetaScope);

        Bound fb = SeekField(in oldestReader, oldestMetaScope, PersistedSnapshot.MetadataFromBlockKey);
        Bound fh = SeekField(in oldestReader, oldestMetaScope, PersistedSnapshot.MetadataFromHashKey);
        Bound tb = SeekField(in newestReader, newestMetaScope, PersistedSnapshot.MetadataToBlockKey);
        Bound th = SeekField(in newestReader, newestMetaScope, PersistedSnapshot.MetadataToHashKey);
        Bound vb = SeekField(in newestReader, newestMetaScope, PersistedSnapshot.MetadataVersionKey);

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
        byte[] refIdsValue = new byte[refIds.Count * 2];
        int idx = 0;
        foreach (ushort id in refIds)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(refIdsValue.AsSpan(idx * 2, 2), id);
            idx++;
        }

        using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, PersistedSnapshot.MetadataKeyLength);

        // Emit all keys in sorted ASCII order. NUL-padding to 10 bytes preserves the
        // original ASCII sort order:
        // "from_block" < "from_hash\0" < "noderefs\0\0" < "ref_ids\0\0\0" < "to_block\0\0" < "to_hash\0\0\0" < "version\0\0\0"
        builder.Add(PersistedSnapshot.MetadataFromBlockKey, fromBlock);
        builder.Add(PersistedSnapshot.MetadataFromHashKey, fromHash);
        builder.Add(PersistedSnapshot.MetadataNodeRefsKey, [0x01]);
        builder.Add(PersistedSnapshot.MetadataRefIdsKey, refIdsValue);
        builder.Add(PersistedSnapshot.MetadataToBlockKey, toBlock);
        builder.Add(PersistedSnapshot.MetadataToHashKey, toHash);
        builder.Add(PersistedSnapshot.MetadataVersionKey, version);

        builder.Build();
    }

    /// <summary>
    /// Specialised slot merger: outer 30-byte BTree, inner 2-byte BTree (suffix → slot value).
    /// Emits bloom adds inline from the merged stream so the compactor doesn't need a
    /// separate per-source slot-tree walk just to populate the bloom. The merged-stream
    /// adds skip duplicates that newest-wins merge collapses; capacity is sized as the
    /// sum-of-sources count in <see cref="PersistedSnapshotCompactor"/>, which over-sizes
    /// after dedup — harmless (false-positive rate is the same or strictly better).
    /// </summary>
    private static void NWayNestedStreamingSlotMerge<TWriter, TReader, TPin>(
        HsstEnumerator[] outerEnums, Span<bool> outerHasMore, int n,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ref TWriter writer,
        BloomFilter? bloom, ulong addrBloomKey) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        const int OuterKeyLen = 30;
        using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, OuterKeyLen, new HsstBTreeOptions { MinSeparatorLength = 4 });

        using NativeMemoryList<int> matchingSourcesList = new(n, n);
        Span<int> matchingSources = matchingSourcesList.AsSpan();

        // Cache outer 30-byte keys (stride 32 for alignment).
        const int OuterStride = 32;
        Span<byte> outerKeyBuf = stackalloc byte[n * OuterStride];
        for (int i = 0; i < n; i++)
        {
            if (!outerHasMore[i]) continue;
            WholeReadSessionReader r = Reader(views[i]);
            outerEnums[i].CopyCurrentLogicalKey(in r, outerKeyBuf.Slice(i * OuterStride, OuterKeyLen));
        }

        // fullSlot composes (outer 30 ⨁ inner 2) for the bloom hash; first 30 bytes are
        // refreshed at each new outer key, last 2 bytes are filled per emitted inner key.
        Span<byte> fullSlot = stackalloc byte[32];

        while (true)
        {
            int minIdx = -1;
            for (int i = 0; i < n; i++)
            {
                if (!outerHasMore[i]) continue;
                if (minIdx < 0) { minIdx = i; continue; }
                ReadOnlySpan<byte> kI = outerKeyBuf.Slice(i * OuterStride, OuterKeyLen);
                ReadOnlySpan<byte> kM = outerKeyBuf.Slice(minIdx * OuterStride, OuterKeyLen);
                if (kI.SequenceCompareTo(kM) < 0) minIdx = i;
            }
            if (minIdx < 0) break;

            ReadOnlySpan<byte> minKey = outerKeyBuf.Slice(minIdx * OuterStride, OuterKeyLen);
            if (bloom is not null)
                minKey.CopyTo(fullSlot[..OuterKeyLen]);

            // Collect matching sources for this outer key.
            int matchCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (!outerHasMore[i]) continue;
                ReadOnlySpan<byte> kI = outerKeyBuf.Slice(i * OuterStride, OuterKeyLen);
                if (kI.SequenceCompareTo(minKey) == 0)
                    matchingSources[matchCount++] = i;
            }

            // Always rebuild the inner BTree against the destination writer's position
            // (alignment/padding depends on it). Inner merge with cached 2-byte keys;
            // emit bloom adds inline so the source slot tree is walked once total.
            ref TWriter innerWriter = ref builder.BeginValueWrite();
            NWayInnerSlotMerge<TWriter, TReader, TPin>(
                outerEnums, matchingSources, matchCount, views,
                ref innerWriter, bloom, addrBloomKey, fullSlot);
            builder.FinishValueWrite(minKey);

            // Advance matching, refilling cached outer keys.
            for (int j = 0; j < matchCount; j++)
            {
                int i = matchingSources[j];
                WholeReadSessionReader r = Reader(views[i]);
                outerHasMore[i] = outerEnums[i].MoveNext(in r);
                if (outerHasMore[i])
                    outerEnums[i].CopyCurrentLogicalKey(in r, outerKeyBuf.Slice(i * OuterStride, OuterKeyLen));
            }
        }

        builder.Build();
    }

    /// <summary>
    /// Inner BTree merge for the fused slot path. Same structure as <see cref="NWayInnerMerge{TWriter, TReader, TPin}"/>
    /// but with a fixed 2-byte inner key, an inline bloom-add on each emitted key, and
    /// uses the caller-provided <paramref name="fullSlot"/> scratch (outer 30 bytes
    /// already filled).
    /// </summary>
    private static void NWayInnerSlotMerge<TWriter, TReader, TPin>(
        HsstEnumerator[] outerEnums, ReadOnlySpan<int> matchingSources, int matchCount,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ref TWriter writer,
        BloomFilter? bloom, ulong addrBloomKey,
        Span<byte> fullSlot) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        const int InnerKeyLen = 2;
        using ArrayPoolList<HsstEnumerator> innerEnums = new(matchCount, matchCount);
        using NativeMemoryList<bool> innerHasMore = new(matchCount, matchCount);
        Span<byte> keyBuf = stackalloc byte[matchCount * InnerKeyLen];

        try
        {
            for (int j = 0; j < matchCount; j++)
            {
                int srcIdx = matchingSources[j];
                Bound vb = outerEnums[srcIdx].CurrentValue;
                WholeReadSessionReader r = Reader(views[srcIdx]);
                innerEnums[j] = new HsstEnumerator(in r, new Bound(vb.Offset, vb.Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
                if (innerHasMore[j])
                    innerEnums[j].CopyCurrentLogicalKey(in r, keyBuf.Slice(j * InnerKeyLen, InnerKeyLen));
            }

            using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, InnerKeyLen, new HsstBTreeOptions { MinSeparatorLength = 2 });
            while (true)
            {
                int minIdx = -1;
                for (int j = 0; j < matchCount; j++)
                {
                    if (!innerHasMore[j]) continue;
                    if (minIdx < 0) { minIdx = j; continue; }
                    ReadOnlySpan<byte> kJ = keyBuf.Slice(j * InnerKeyLen, InnerKeyLen);
                    ReadOnlySpan<byte> kM = keyBuf.Slice(minIdx * InnerKeyLen, InnerKeyLen);
                    int cmp = kJ.SequenceCompareTo(kM);
                    if (cmp < 0) minIdx = j;
                    else if (cmp == 0) minIdx = j; // newer wins
                }
                if (minIdx < 0) break;

                Bound vb = innerEnums[minIdx].CurrentValue;
                WholeReadSessionReader rMin = Reader(views[matchingSources[minIdx]]);
                ReadOnlySpan<byte> minKey = keyBuf.Slice(minIdx * InnerKeyLen, InnerKeyLen);
                using NoOpPin valPin = rMin.PinBuffer(vb.Offset, vb.Length);
                builder.Add(minKey, valPin.Buffer);

                // Inline bloom-add: fullSlot[0..30] already holds the outer prefix; copy
                // the 2-byte suffix in and hash. Matches AddSlotKeysToBloom's composition.
                if (bloom is not null)
                {
                    minKey.CopyTo(fullSlot[30..]);
                    ulong s0 = MemoryMarshal.Read<ulong>(fullSlot);
                    ulong s1 = MemoryMarshal.Read<ulong>(fullSlot[8..]);
                    ulong s2 = MemoryMarshal.Read<ulong>(fullSlot[16..]);
                    ulong s3 = MemoryMarshal.Read<ulong>(fullSlot[24..]);
                    bloom.Add(addrBloomKey ^ s0 ^ s1 ^ s2 ^ s3);
                }

                for (int j = 0; j < matchCount; j++)
                {
                    if (j == minIdx || !innerHasMore[j]) continue;
                    ReadOnlySpan<byte> kJ = keyBuf.Slice(j * InnerKeyLen, InnerKeyLen);
                    if (kJ.SequenceCompareTo(minKey) == 0)
                    {
                        WholeReadSessionReader rJ = Reader(views[matchingSources[j]]);
                        innerHasMore[j] = innerEnums[j].MoveNext(in rJ);
                        if (innerHasMore[j])
                            innerEnums[j].CopyCurrentLogicalKey(in rJ, keyBuf.Slice(j * InnerKeyLen, InnerKeyLen));
                    }
                }
                {
                    WholeReadSessionReader r = Reader(views[matchingSources[minIdx]]);
                    innerHasMore[minIdx] = innerEnums[minIdx].MoveNext(in r);
                    if (innerHasMore[minIdx])
                        innerEnums[minIdx].CopyCurrentLogicalKey(in r, keyBuf.Slice(minIdx * InnerKeyLen, InnerKeyLen));
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
    /// Walk the slot HSST at <paramref name="slotScope"/> (outer 30-byte prefix → inner 2-byte
    /// suffix) and add every <c>(outer ⨁ inner)</c> slot key to <paramref name="bloom"/>. Used
    /// by the matchCount==1 / slotSourceCount==1 byte-copy fast paths, which bypass the
    /// streaming merge that would otherwise fold the same bloom adds inline (see
    /// <see cref="NWayInnerSlotMerge"/>). Composition matches that inline path:
    /// <c>addrKey ^ s0 ^ s1 ^ s2 ^ s3</c> over the 32-byte concatenation.
    /// </summary>
    private static void AddSlotKeysToBloom<TReader, TPin>(
        scoped in TReader reader, Bound slotScope, ulong addrKey, BloomFilter bloom)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        Span<byte> fullSlot = stackalloc byte[32];
        HsstEnumerator<TReader, TPin> outerEnum = new(in reader, slotScope);
        while (outerEnum.MoveNext(in reader))
        {
            outerEnum.CopyCurrentLogicalKey(in reader, fullSlot[..30]);
            Bound ovb = outerEnum.CurrentValue;
            HsstEnumerator<TReader, TPin> innerEnum = new(in reader, ovb);
            while (innerEnum.MoveNext(in reader))
            {
                innerEnum.CopyCurrentLogicalKey(in reader, fullSlot[30..]);
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
