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
    private const int AddressKeyLength = PersistedSnapshot.AddressKeyLength;          // 20 — column 0x01 outer key
    private const int AddressHashPrefixLength = PersistedSnapshot.AddressHashPrefixLength;  // 20 — column 0x02 outer key

    private static readonly Comparison<TreePath> StateNodeComparer = (a, b) =>
    {
        int cmp = a.Path.Bytes.SequenceCompareTo(b.Path.Bytes);
        return cmp != 0 ? cmp : a.Length.CompareTo(b.Length);
    };

    // Sorts storage-trie node keys by 20-byte address-hash prefix (matching the column-0x02
    // outer key) and then by encoded path so per-addressHash slices are contiguous and the
    // inner HSST keys are in sorted order.
    private static readonly Comparison<(ValueHash256 AddrHash, TreePath Path)> StorageNodeComparer = (a, b) =>
    {
        int cmp = a.AddrHash.Bytes[..AddressHashPrefixLength].SequenceCompareTo(b.AddrHash.Bytes[..AddressHashPrefixLength]);
        if (cmp != 0) return cmp;
        cmp = a.Path.Path.Bytes.SequenceCompareTo(b.Path.Path.Bytes);
        return cmp != 0 ? cmp : a.Path.Length.CompareTo(b.Path.Length);
    };

    // Sorts slot entries by raw Address bytes (matching the column-0x01 outer key) then by
    // slot value, so per-address slices are contiguous and slot keys within a slice are in
    // sorted big-endian order.
    private static readonly Comparison<((ValueAddress Addr, UInt256 Slot) Key, SlotValue? Value)> StoragesByAddressComparer = (a, b) =>
    {
        int cmp = a.Key.Addr.AsSpan.SequenceCompareTo(b.Key.Addr.AsSpan);
        if (cmp != 0) return cmp;
        return a.Key.Slot.CompareTo(b.Key.Slot);
    };

    private static readonly Comparison<ValueAddress> ValueAddressComparer = (a, b) =>
        a.AsSpan.SequenceCompareTo(b.AsSpan);

    public static void Build<TWriter, TReader, TPin>(Snapshot snapshot, ref TWriter writer, BlobArenaWriter blobWriter, BloomFilter? bloom = null, BloomFilter? trieBloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // To stay off the LOH, we keep only the unmanaged sort keys in NativeMemoryList
        // (off-heap) and re-fetch the TrieNode value from the source ConcurrentDictionary
        // at column-write time.
        NativeMemoryList<TreePath> stateTopKeys = null!, stateCompactKeys = null!, stateFallbackKeys = null!;
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storTopKeys = null!, storCompactKeys = null!, storFallbackKeys = null!;
        // Slot entries sorted by raw 20-byte Address bytes (matching the column-0x01 outer
        // key), then by big-endian slot. No address hashing during build — column 0x01 is
        // keyed by raw Address, and slot bloom keys derive from raw address bytes too.
        NativeMemoryList<((ValueAddress Addr, UInt256 Slot) Key, SlotValue? Value)> sortedStorages = null!;
        // Sorted list of unique raw 20-byte Addresses covering accounts / SD / storages.
        // Drives the column-0x01 outer iteration; per-address slots are matched by raw
        // address equality with sortedStorages.
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
                // SD / slots) as raw Address bytes. No hashing here; column 0x01 keys
                // directly on the 20 raw Address bytes.
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

                NativeMemoryList<ValueAddress> addrs = new(Math.Max(1, seen.Count));
                foreach (HashedKey<Address> addr in seen)
                    addrs.Add(new ValueAddress(addr.Key.Bytes));
                addrs.Sort(ValueAddressComparer);

                storages.Sort(StoragesByAddressComparer);

                sortedStorages = storages;
                uniqueAddresses = addrs;
            });

        HsstDenseByteIndexBuilder<TWriter> outer = new(ref writer);
        try
        {
            // Column 0x00: Metadata
            WriteMetadataColumn<TWriter, TReader, TPin>(ref outer, snapshot, blobWriter.BlobArenaId);

            // Column 0x01: Per-Address column. Sub-tags 0x04 (slots), 0x05 (account RLP),
            // 0x06 (SD). Outer key is the raw 20-byte Address.
            WriteAccountColumn<TWriter, TReader, TPin>(ref outer, snapshot, sortedStorages, uniqueAddresses,
                blobWriter, bloom);

            // Column 0x02: Per-AddressHash storage trie column. Sub-tags 0x01 (top),
            // 0x02 (compact), 0x03 (fallback). Outer key is the 20-byte address-hash prefix.
            WriteStorageTrieColumn<TWriter, TReader, TPin>(ref outer, snapshot,
                storTopKeys, storCompactKeys, storFallbackKeys, blobWriter, trieBloom);

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
        NativeMemoryList<((ValueAddress Addr, UInt256 Slot) Key, SlotValue? Value)> sortedStorages,
        NativeMemoryList<ValueAddress> uniqueAddresses,
        BlobArenaWriter blobWriter,
        BloomFilter? bloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        const int slotPrefixLength = 30;
        const int slotSuffixLength = 32 - slotPrefixLength;

        // Address-level HSST keyed by 20 raw Address bytes.
        ref TWriter addressWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilder<TWriter, TReader, TPin> addressLevel = new(ref addressWriter, AddressKeyLength, new HsstBTreeOptions
        {
            MinSeparatorLength = 4,
        }, expectedKeyCount: uniqueAddresses.Count);
        // Slim-account RLP for any single account fits comfortably in 256 bytes (4×u256 fields
        // plus framing). Pool the scratch so it doesn't allocate per WriteAccountColumn call.
        byte[] rlpBuffer = ArrayPool<byte>.Shared.Rent(256);
        RlpStream rlpStream = new(rlpBuffer);
        Span<byte> slotKey = stackalloc byte[32];
        Span<byte> currentPrefixBuf = stackalloc byte[slotPrefixLength];
        // Reusable work buffer for the slot prefix (30-byte) HSST BTree builder.
        // Constructed once per address. Sharing the buffer struct across every
        // iteration of the address loop avoids the rent/return churn that would
        // otherwise hit ArrayPool / NativeMemory once per slot subtree.
        // Declared as a plain local (not `using`) so it can be passed by ref into
        // the builder constructor — the compiler forbids `ref` on `using` variables.
        // The slot suffix layer now uses TwoByteSlotValue[Large] which pool internally.
        HsstBTreeBuilderBuffers slotPrefixBuffers = new();

        // Pooled staging buffer for the per-prefix sub-slot HSST. The slot-prefix
        // BTree is built in key-first mode (IndexType.BTreeKeyFirst) so its outer
        // entry layout is [FullKey][LEB128][Value] — the value length must be known
        // before laying down the LEB128, which means the sub-slot bytes have to be
        // staged in their entirety first. The buffer is Reset() between iterations
        // so the underlying NativeMemory allocation amortizes across the address
        // and prefix loops.
        using PooledByteBufferWriter slotSuffixBuffer = new(4096);
        int storageIdx = 0;

        for (int addrIdx = 0; addrIdx < uniqueAddresses.Count; addrIdx++)
        {
            ValueAddress vaddr = uniqueAddresses[addrIdx];
            ReadOnlySpan<byte> addressBytes = vaddr.AsSpan;
            // uniqueAddresses came from accounts/SD/storages only, so every entry has a real
            // Address; no null-guard needed for account/SD/slot lookups below.
            Address address = vaddr.ToAddress();

            ulong addrBloomKey = 0;
            if (bloom is not null)
            {
                addrBloomKey = PersistedSnapshotBloomBuilder.AddressKey(addressBytes);
                bloom.Add(addrBloomKey);
            }

            // Begin per-address HSST. Sub-tags 0x04/0x05/0x06; DenseByteIndex addresses
            // entries by tag-byte directly and gap-fills missing positions with length-0
            // values. Sub-tag value-presence semantics:
            //   0x04 slots: nested HSST(SlotPrefix(30) → nested HSST(SlotSuffix(2) → bytes))
            //   0x05 account: [] absent / [0x00] deleted / RLP-bytes present
            //   0x06 SD: [] absent / [0x00] destructed / [0x01] new account
            // (Storage-trie sub-tags 0x01..0x03 live in column 0x02 now, keyed by addressHash.)
            ref TWriter perAddrWriter = ref addressLevel.BeginValueWrite();
            using HsstDenseByteIndexBuilder<TWriter> perAddr = new(ref perAddrWriter);

            // Sub-tag 0x04: Slots — sortedStorages is sorted by raw Address; advance the
            // cursor over the contiguous slot run for this address.
            bool hasStorage = storageIdx < sortedStorages.Count &&
                sortedStorages[storageIdx].Key.Addr.AsSpan.SequenceEqual(addressBytes);
            if (hasStorage)
            {
                ref TWriter slotWriter = ref perAddr.BeginValueWrite();
                using HsstBTreeBuilder<TWriter, TReader, TPin> prefixLevel = new(ref slotWriter, ref slotPrefixBuffers, slotPrefixLength, new HsstBTreeOptions { MinSeparatorLength = 4 }, keyFirst: true);

                while (storageIdx < sortedStorages.Count &&
                    sortedStorages[storageIdx].Key.Addr.AsSpan.SequenceEqual(addressBytes))
                {
                    sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                    slotKey[..slotPrefixLength].CopyTo(currentPrefixBuf);
                    ReadOnlySpan<byte> currentPrefix = currentPrefixBuf;

                    // Look ahead over the current prefix group to total its value bytes.
                    // TwoByteSlotValue caps the data region at ushort.MaxValue; fall back to
                    // BTree when a group's payload overflows. In practice, per-prefix groups
                    // are tiny (a handful of slots) so the look-ahead is cheap and the
                    // u16 cap is virtually never hit.
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
                        groupValueBytes += v.HasValue ? v.Value.AsReadOnlySpan.WithoutLeadingZeros().Length : 0;
                        groupEnd++;
                    }

                    slotSuffixBuffer.Reset();
                    ref PooledByteBufferWriter.Writer suffixWriter = ref slotSuffixBuffer.GetWriter();
                    if (HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(groupValueBytes))
                    {
                        using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> suffixLevel = new(ref suffixWriter);
                        for (int i = groupStart; i < groupEnd; i++)
                        {
                            sortedStorages[i].Key.Slot.ToBigEndian(slotKey);
                            if (bloom is not null)
                                bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, slotKey));
                            SlotValue? value = sortedStorages[i].Value;
                            ReadOnlySpan<byte> suffixKey = slotKey.Slice(slotPrefixLength, slotSuffixLength);
                            ReadOnlySpan<byte> payload = value.HasValue
                                ? value.Value.AsReadOnlySpan.WithoutLeadingZeros()
                                : [];
                            suffixLevel.Add(suffixKey, payload);
                        }
                        suffixLevel.Build();
                    }
                    else
                    {
                        using HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer> suffixLevel = new(ref suffixWriter);
                        for (int i = groupStart; i < groupEnd; i++)
                        {
                            sortedStorages[i].Key.Slot.ToBigEndian(slotKey);
                            if (bloom is not null)
                                bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, slotKey));
                            SlotValue? value = sortedStorages[i].Value;
                            ReadOnlySpan<byte> suffixKey = slotKey.Slice(slotPrefixLength, slotSuffixLength);
                            ReadOnlySpan<byte> payload = value.HasValue
                                ? value.Value.AsReadOnlySpan.WithoutLeadingZeros()
                                : [];
                            suffixLevel.Add(suffixKey, payload);
                        }
                        suffixLevel.Build();
                    }
                    storageIdx = groupEnd;
                    prefixLevel.Add(currentPrefix, slotSuffixBuffer.WrittenSpan);
                }

                prefixLevel.Build();
                perAddr.FinishValueWrite(PersistedSnapshot.SlotSubTag);
            }

            // Sub-tag 0x05: Account. Present-marker encoding: [0x00] deleted, RLP-bytes
            // present; length 0 = absent (gap-filled). Slim account RLP starts with a
            // list header (0xc0+) so 0x00 first-byte is unambiguous.
            if (snapshot.TryGetAccount(address, out Account? account))
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
            if (snapshot.Content.SelfDestructedStorageAddresses.TryGetValue(address, out bool sdValue))
            {
                perAddr.Add(PersistedSnapshot.SelfDestructSubTag, sdValue ? [0x01] : [0x00]);
            }

            perAddr.Build();
            addressLevel.FinishValueWrite(addressBytes);
        }

        addressLevel.Build();
        outer.FinishValueWrite(PersistedSnapshot.AccountColumnTag);
        ArrayPool<byte>.Shared.Return(rlpBuffer);
        slotPrefixBuffers.Dispose();
    }

    /// <summary>
    /// Write the storage-trie column (outer tag 0x02) keyed by 20-byte address-hash prefix.
    /// Per addressHash the inner HSST carries sub-tags 0x01 (top, 4-byte path), 0x02 (compact,
    /// 8-byte path), and 0x03 (fallback, 33-byte path) — values are 6-byte <see cref="NodeRef"/>s
    /// pointing into the blob arena. Inputs are pre-sorted by 20-byte hash prefix then by
    /// encoded path.
    /// </summary>
    private static void WriteStorageTrieColumn<TWriter, TReader, TPin>(
        ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storTop,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storCompact,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storFallback,
        BlobArenaWriter blobWriter,
        BloomFilter? trieBloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // Pre-count unique address-hash prefixes by N-way-walking the three sorted lists.
        // Used to size the BTree builder and to early-return when there are no storage-trie
        // nodes at all (we still emit an empty column entry to keep outer offsets stable).
        int uniqueAddrHashCount = CountUniqueStorageAddrHashes(storTop, storCompact, storFallback);

        ref TWriter columnWriter = ref outer.BeginValueWrite();
        using HsstBTreeBuilder<TWriter, TReader, TPin> addressLevel = new(ref columnWriter, AddressHashPrefixLength, new HsstBTreeOptions
        {
            MinSeparatorLength = 4,
        }, expectedKeyCount: uniqueAddrHashCount);

        Span<byte> topPathKey = stackalloc byte[4];
        Span<byte> compactPathKey = stackalloc byte[8];
        Span<byte> fallbackPathKey = stackalloc byte[33];
        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];

        int storTopIdx = 0, storCompactIdx = 0, storFallbackIdx = 0;

        while (storTopIdx < storTop.Count || storCompactIdx < storCompact.Count || storFallbackIdx < storFallback.Count)
        {
            // Pick the smallest 20-byte hash prefix across the three sorted lists.
            ValueHash256 addressHash = PickMinAddrHash(
                storTop, storTopIdx,
                storCompact, storCompactIdx,
                storFallback, storFallbackIdx);
            ReadOnlySpan<byte> addressHashPrefix = addressHash.Bytes[..AddressHashPrefixLength];
            Hash256 addrRefForStorageNode = new(in addressHash);

            ref TWriter perAddrHashWriter = ref addressLevel.BeginValueWrite();
            using HsstDenseByteIndexBuilder<TWriter> perAddrHash = new(ref perAddrHashWriter);

            // Sub-tag 0x01: top (4-byte path keys).
            int topStart = storTopIdx;
            while (storTopIdx < storTop.Count &&
                storTop[storTopIdx].AddrHash.Bytes[..AddressHashPrefixLength].SequenceEqual(addressHashPrefix))
                storTopIdx++;
            if (topStart < storTopIdx)
            {
                ref TWriter topWriter = ref perAddrHash.BeginValueWrite();
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
                perAddrHash.FinishValueWrite(PersistedSnapshot.StorageTopSubTag);
            }

            // Sub-tag 0x02: compact (8-byte path keys).
            int compactStart = storCompactIdx;
            while (storCompactIdx < storCompact.Count &&
                storCompact[storCompactIdx].AddrHash.Bytes[..AddressHashPrefixLength].SequenceEqual(addressHashPrefix))
                storCompactIdx++;
            if (compactStart < storCompactIdx)
            {
                ref TWriter compactWriter = ref perAddrHash.BeginValueWrite();
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
                perAddrHash.FinishValueWrite(PersistedSnapshot.StorageCompactSubTag);
            }

            // Sub-tag 0x03: fallback (33-byte path keys).
            int fallbackStart = storFallbackIdx;
            while (storFallbackIdx < storFallback.Count &&
                storFallback[storFallbackIdx].AddrHash.Bytes[..AddressHashPrefixLength].SequenceEqual(addressHashPrefix))
                storFallbackIdx++;
            if (fallbackStart < storFallbackIdx)
            {
                ref TWriter fbWriter = ref perAddrHash.BeginValueWrite();
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
                perAddrHash.FinishValueWrite(PersistedSnapshot.StorageFallbackSubTag);
            }

            perAddrHash.Build();
            addressLevel.FinishValueWrite(addressHashPrefix);
        }

        addressLevel.Build();
        outer.FinishValueWrite(PersistedSnapshot.StorageTrieColumnTag);
    }

    /// <summary>
    /// Count distinct 20-byte address-hash prefixes across the three pre-sorted
    /// storage-trie partition lists by N-way walking them.
    /// </summary>
    private static int CountUniqueStorageAddrHashes(
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storTop,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storCompact,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> storFallback)
    {
        int topIdx = 0, compactIdx = 0, fallbackIdx = 0;
        int unique = 0;
        ValueHash256 last = default;
        bool haveLast = false;
        while (topIdx < storTop.Count || compactIdx < storCompact.Count || fallbackIdx < storFallback.Count)
        {
            ValueHash256 next = PickMinAddrHash(storTop, topIdx, storCompact, compactIdx, storFallback, fallbackIdx);
            if (!haveLast || !next.Bytes[..AddressHashPrefixLength].SequenceEqual(last.Bytes[..AddressHashPrefixLength]))
            {
                unique++;
                last = next;
                haveLast = true;
            }
            ReadOnlySpan<byte> prefix = next.Bytes[..AddressHashPrefixLength];
            while (topIdx < storTop.Count && storTop[topIdx].AddrHash.Bytes[..AddressHashPrefixLength].SequenceEqual(prefix)) topIdx++;
            while (compactIdx < storCompact.Count && storCompact[compactIdx].AddrHash.Bytes[..AddressHashPrefixLength].SequenceEqual(prefix)) compactIdx++;
            while (fallbackIdx < storFallback.Count && storFallback[fallbackIdx].AddrHash.Bytes[..AddressHashPrefixLength].SequenceEqual(prefix)) fallbackIdx++;
        }
        return unique;
    }

    private static ValueHash256 PickMinAddrHash(
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> a, int aIdx,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> b, int bIdx,
        NativeMemoryList<(ValueHash256 AddrHash, TreePath Path)> c, int cIdx)
    {
        bool hasA = aIdx < a.Count;
        bool hasB = bIdx < b.Count;
        bool hasC = cIdx < c.Count;
        ValueHash256 best = default;
        bool haveBest = false;
        if (hasA) { best = a[aIdx].AddrHash; haveBest = true; }
        if (hasB && (!haveBest || b[bIdx].AddrHash.Bytes[..AddressHashPrefixLength].SequenceCompareTo(best.Bytes[..AddressHashPrefixLength]) < 0))
        { best = b[bIdx].AddrHash; haveBest = true; }
        if (hasC && (!haveBest || c[cIdx].AddrHash.Bytes[..AddressHashPrefixLength].SequenceCompareTo(best.Bytes[..AddressHashPrefixLength]) < 0))
            best = c[cIdx].AddrHash;
        return best;
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
