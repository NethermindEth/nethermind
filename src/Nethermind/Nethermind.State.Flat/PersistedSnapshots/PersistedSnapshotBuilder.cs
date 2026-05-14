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
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;

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
        // Reusable work buffers for the slot prefix (30-byte) and slot suffix (2-byte)
        // HSST builders. The prefix builder is constructed once per address; the suffix
        // builder once per prefix group per address. Sharing the buffer struct across
        // every iteration of the address loop avoids the rent/return churn that would
        // otherwise hit ArrayPool / NativeMemory once per slot subtree.
        // Declared as plain locals (not `using`) so they can be passed by ref into the
        // builder constructors — the compiler forbids `ref` on `using` variables.
        HsstBTreeBuilderBuffers slotPrefixBuffers = new();
        HsstBTreeBuilderBuffers slotSuffixBuffers = new();
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
                using HsstBTreeBuilder<TWriter, TReader, TPin> prefixLevel = new(ref slotWriter, ref slotPrefixBuffers, slotPrefixLength, new HsstBTreeOptions { MinSeparatorLength = 4 });

                while (storageIdx < sortedStorages.Count &&
                    sortedStorages[storageIdx].Key.AddrHash.Equals(addressHash))
                {
                    sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                    slotKey[..slotPrefixLength].CopyTo(currentPrefixBuf);
                    ReadOnlySpan<byte> currentPrefix = currentPrefixBuf;

                    ref TWriter suffixWriter = ref prefixLevel.BeginValueWrite();
                    using HsstBTreeBuilder<TWriter, TReader, TPin> suffixLevel = new(ref suffixWriter, ref slotSuffixBuffers, keyLength: slotSuffixLength,
                        new HsstBTreeOptions { MinSeparatorLength = slotSuffixLength });

                    while (storageIdx < sortedStorages.Count &&
                        sortedStorages[storageIdx].Key.AddrHash.Equals(addressHash))
                    {
                        sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                        if (!slotKey[..slotPrefixLength].SequenceEqual(currentPrefix))
                            break;

                        // Per-slot bloom add keyed on the full 32-byte slot; matches the
                        // reader-side hash in ReadOnlySnapshotBundle.GetSlot.
                        if (bloom is not null)
                            bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, slotKey));

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
        slotSuffixBuffers.Dispose();
        slotPrefixBuffers.Dispose();
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
}
