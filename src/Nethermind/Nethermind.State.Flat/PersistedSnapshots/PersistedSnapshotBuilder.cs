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
using HsstMergeEnumerator = Nethermind.State.Flat.Hsst.HsstMergeEnumerator<Nethermind.State.Flat.Storage.WholeReadSessionReader, Nethermind.State.Flat.Hsst.NoOpPin>;

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
/// <see cref="NodeRef.ValueLengthOffset"/> is a 32-bit int that addresses bytes inside
/// the referenced Full snapshot, so any byte past 2 GiB is unreachable from a Linked
/// snapshot's NodeRef. <see cref="ConvertFullToLinked"/> enforces this with a
/// <c>checked((int)colOff)</c> cast on each column offset.
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

    private static readonly Comparison<(TreePath Path, TrieNode Node)> StateNodeComparer = (a, b) =>
    {
        int cmp = a.Path.Path.Bytes.SequenceCompareTo(b.Path.Path.Bytes);
        return cmp != 0 ? cmp : a.Path.Length.CompareTo(b.Path.Length);
    };

    // Sorts storage-trie nodes by 20-byte address-hash prefix (matching the column-0x01
    // outer key) and then by encoded path so per-address slices are contiguous and the
    // inner HSST keys are in sorted order.
    private static readonly Comparison<((Hash256 Addr, TreePath Path) Key, TrieNode Node)> StorageNodeComparer = (a, b) =>
    {
        int cmp = a.Key.Addr.Bytes[..StorageHashPrefixLength].SequenceCompareTo(b.Key.Addr.Bytes[..StorageHashPrefixLength]);
        if (cmp != 0) return cmp;
        cmp = a.Key.Path.Path.Bytes.SequenceCompareTo(b.Key.Path.Path.Bytes);
        return cmp != 0 ? cmp : a.Key.Path.Length.CompareTo(b.Key.Path.Length);
    };

    /// <summary>
    /// Build an <see cref="HsstReader{SpanByteReader,NoOpPin}"/> over <paramref name="data"/>,
    /// exact-seek for <paramref name="key"/>, and slice the result span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGet(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        SpanByteReader r = new(data);
        HsstReader<SpanByteReader, NoOpPin> hsst = new(in r);
        if (!hsst.TrySeek(key, out _)) { value = default; return false; }
        Bound b = hsst.GetBound();
        value = data.Slice(checked((int)b.Offset), checked((int)b.Length));
        return true;
    }

    /// <summary>
    /// Like <see cref="TryGet"/> but returns the matched entry's offset+length within
    /// <paramref name="data"/> without producing a span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetBound(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out int offset, out int length)
    {
        SpanByteReader r = new(data);
        HsstReader<SpanByteReader, NoOpPin> hsst = new(in r);
        if (!hsst.TrySeek(key, out _)) { offset = 0; length = 0; return false; }
        Bound b = hsst.GetBound();
        offset = checked((int)b.Offset);
        length = checked((int)b.Length);
        return true;
    }

    /// <summary>
    /// Reader-based <see cref="TryGetBound"/>: seek <paramref name="key"/> within
    /// <paramref name="scope"/> of <paramref name="reader"/>. Returned offset is
    /// reader-absolute.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetBound<TReader, TPin>(
        scoped in TReader reader, Bound scope,
        scoped ReadOnlySpan<byte> key,
        out long offset, out long length)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        HsstReader<TReader, TPin> hsst = new(in reader, scope);
        if (!hsst.TrySeek(key, out _)) { offset = 0; length = 0; return false; }
        Bound b = hsst.GetBound();
        offset = b.Offset;
        length = b.Length;
        return true;
    }

    public static void Build<TWriter>(Snapshot snapshot, ref TWriter writer, BloomFilter? bloom = null, BloomFilter? trieBloom = null) where TWriter : IByteBufferWriter
    {
        // Declare mutable locals populated by the parallel jobs below.
        ArrayPoolList<(TreePath Path, TrieNode Node)> stateTop = null!, stateCompact = null!, stateFallback = null!;
        ArrayPoolList<((Hash256 Addr, TreePath Path) Key, TrieNode Node)> storCompact = null!, storFallback = null!;
        ArrayPoolList<((Address Addr, UInt256 Slot) Key, SlotValue? Value)> sortedStorages = null!;
        // Per-address bookkeeping for the unified column 0x01:
        //   uniqueAddresses: every Address that has any of (account, slot, SD, storage-trie
        //     compact, storage-trie fallback). Sorted by hash-prefix so a single linear walk
        //     across the address list, the slot list, and the two storage-trie lists can
        //     line up positions for each address.
        //   uniqueAddressHashes[i] = keccak(uniqueAddresses[i].Bytes) — pre-computed once
        //     so we do not re-hash per sub-tag. uniqueAddresses and uniqueAddressHashes are
        //     parallel arrays.
        ArrayPoolList<Address> uniqueAddresses = null!;
        ArrayPoolList<ValueHash256> uniqueAddressHashes = null!;

        // Parallel extraction + sort: three independent jobs over disjoint dictionaries.
        Parallel.Invoke(
            () =>
            {
                // Job A: state trie nodes — partition into top/compact/fallback, then sort.
                ArrayPoolList<(TreePath, TrieNode)> top = new(0);
                ArrayPoolList<(TreePath, TrieNode)> compact = new(snapshot.StateNodesCount);
                ArrayPoolList<(TreePath, TrieNode)> fallback = new(0);
                foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes)
                {
                    if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
                    TreePath path = kv.Key;
                    if (path.Length <= TopPathThreshold) top.Add((path, kv.Value));
                    else if (path.Length <= CompactPathThreshold) compact.Add((path, kv.Value));
                    else fallback.Add((path, kv.Value));
                    kv.Value.IsPersisted = true;
                    kv.Value.PrunePersistedRecursively(1);
                }
                Parallel.Invoke(
                    () => top.Sort(StateNodeComparer),
                    () => compact.Sort(StateNodeComparer),
                    () => fallback.Sort(StateNodeComparer));
                stateTop = top; stateCompact = compact; stateFallback = fallback;
            },
            () =>
            {
                // Job B: storage trie nodes — partition into compact/fallback, then sort.
                ArrayPoolList<((Hash256, TreePath), TrieNode)> compact = new(snapshot.StorageNodesCount);
                ArrayPoolList<((Hash256, TreePath), TrieNode)> fallback = new(0);
                foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kv in snapshot.StorageNodes)
                {
                    if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
                    (Hash256 addr, TreePath path) = kv.Key.Key;
                    if (path.Length <= CompactPathThreshold) compact.Add(((addr, path), kv.Value));
                    else fallback.Add(((addr, path), kv.Value));
                    kv.Value.IsPersisted = true;
                    kv.Value.PrunePersistedRecursively(1);
                }
                Parallel.Invoke(
                    () => compact.Sort(StorageNodeComparer),
                    () => fallback.Sort(StorageNodeComparer));
                storCompact = compact; storFallback = fallback;
            },
            () =>
            {
                // Job C: account column prep — collect Address-keyed sources (accounts /
                // SD / slots), pre-hash each address once, and produce a partial unique
                // list. Storage-trie-only address-hashes (no Address available) are merged
                // in after the parallel jobs complete (see below) so this thread doesn't
                // touch storCompact / storFallback while Job B is still populating them.
                using PooledSet<HashedKey<Address>> seen = new();
                foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts)
                    seen.Add(kv.Key);
                foreach (KeyValuePair<HashedKey<Address>, bool> kv in snapshot.SelfDestructedStorageAddresses)
                    seen.Add(kv.Key);

                ArrayPoolList<((Address Addr, UInt256 Slot) Key, SlotValue? Value)> storages =
                    new(Math.Max(1, snapshot.StoragesCount));
                foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> kv in snapshot.Storages)
                {
                    (Address addr, UInt256 slot) = kv.Key.Key;
                    storages.Add(((addr, slot), kv.Value));
                    seen.Add(addr);
                }

                ArrayPoolList<Address> addrs = new(Math.Max(1, seen.Count));
                ArrayPoolList<ValueHash256> hashes = new(Math.Max(1, seen.Count));
                using ArrayPoolList<(Address Addr, ValueHash256 Hash)> pairs = new(Math.Max(1, seen.Count));
                foreach (HashedKey<Address> addr in seen)
                    pairs.Add((addr, ValueKeccak.Compute(addr.Key.Bytes)));
                for (int i = 0; i < pairs.Count; i++)
                {
                    addrs.Add(pairs[i].Addr);
                    hashes.Add(pairs[i].Hash);
                }

                // Preliminary slot sort — final ordering aligns with the merged hash list
                // produced after Parallel.Invoke, but the within-address (slot) ordering is
                // independent so it can settle here.
                Dictionary<AddressAsKey, ValueHash256> addrToHash = new(pairs.Count);
                for (int i = 0; i < pairs.Count; i++)
                    addrToHash[pairs[i].Addr] = pairs[i].Hash;
                storages.Sort((a, b) =>
                {
                    ValueHash256 ah = addrToHash[a.Key.Addr];
                    ValueHash256 bh = addrToHash[b.Key.Addr];
                    int cmp = ah.Bytes[..StorageHashPrefixLength].SequenceCompareTo(bh.Bytes[..StorageHashPrefixLength]);
                    if (cmp != 0) return cmp;
                    return a.Key.Slot.CompareTo(b.Key.Slot);
                });

                sortedStorages = storages;
                uniqueAddresses = addrs;
                uniqueAddressHashes = hashes;
            });

        // After Parallel.Invoke: merge in storage-trie-only address-hashes (those that
        // appear in StorageNodes but not in Accounts/SD/Slots, so Job C didn't see them).
        // We then re-sort the unified list by 20-byte hash prefix so column 0x01 emits
        // outer keys in ascending order; sortedStorages is already keyed by hash prefix
        // and contains only addresses-with-slots so it stays in sync.
        {
            HashSet<ValueHash256> existingHashes = new(uniqueAddressHashes.Count);
            foreach (ValueHash256 h in uniqueAddressHashes)
                existingHashes.Add(h);

            ArrayPoolList<(Address? Addr, ValueHash256 Hash)> combined = new(uniqueAddresses.Count + storCompact.Count + storFallback.Count);
            for (int i = 0; i < uniqueAddresses.Count; i++)
                combined.Add((uniqueAddresses[i], uniqueAddressHashes[i]));

            void AddTrieOnly(((Hash256 Addr, TreePath Path) Key, TrieNode Node) entry)
            {
                ValueHash256 v = entry.Key.Addr.ValueHash256;
                if (existingHashes.Add(v))
                    combined.Add((null, v));
            }
            for (int i = 0; i < storCompact.Count; i++) AddTrieOnly(storCompact[i]);
            for (int i = 0; i < storFallback.Count; i++) AddTrieOnly(storFallback[i]);

            combined.Sort((a, b) =>
                a.Hash.Bytes[..StorageHashPrefixLength].SequenceCompareTo(b.Hash.Bytes[..StorageHashPrefixLength]));

            uniqueAddresses.Clear();
            uniqueAddressHashes.Clear();
            // uniqueAddresses now allows null entries (storage-trie-only address-hashes);
            // we keep it as ArrayPoolList<Address?> via Address? boxing through `Address?`
            // wouldn't work — Address is a reference type, so null is valid.
            for (int i = 0; i < combined.Count; i++)
            {
                uniqueAddresses.Add(combined[i].Addr!);
                uniqueAddressHashes.Add(combined[i].Hash);
            }
            combined.Dispose();
        }

        HsstDenseByteIndexBuilder<TWriter> outer = new(ref writer);
        try
        {
            // Column 0x00: Metadata
            WriteMetadataColumn(ref outer, snapshot);

            // Column 0x01: Unified per-address column. Sub-tags 0x01 (storage trie compact),
            // 0x02 (storage trie fallback), 0x03 (slots), 0x04 (account RLP), 0x05 (SD).
            WriteAccountColumn(ref outer, snapshot, sortedStorages, uniqueAddresses, uniqueAddressHashes,
                storCompact, storFallback, bloom, trieBloom);

            // Column 0x03: State nodes (compact, path length 6-15)
            WriteStateNodesColumnCompact(ref outer, stateCompact, trieBloom);

            // Column 0x05: State top nodes (path length 0-5)
            WriteStateTopNodesColumn(ref outer, stateTop, trieBloom);

            // Column 0x06: State nodes fallback (path length 16+)
            WriteStateNodesColumnFallback(ref outer, stateFallback, trieBloom);

            outer.Build();
        }
        finally
        {
            outer.Dispose();
            sortedStorages?.Dispose();
            uniqueAddresses?.Dispose();
            uniqueAddressHashes?.Dispose();
            stateTop?.Dispose();
            stateCompact?.Dispose();
            stateFallback?.Dispose();
            storCompact?.Dispose();
            storFallback?.Dispose();
        }
    }

    /// <summary>
    /// Estimate of the serialized Full snapshot size, used to size the destination arena
    /// reservation. Capped at 2 GiB — the hard ceiling on a Full snapshot (see the
    /// <see cref="NodeRef.ValueLengthOffset"/> note on the class doc above). Returned as
    /// <see cref="long"/> so callers feeding this into long-typed APIs (e.g. arena
    /// reservations) don't truncate; the cap also keeps the value within
    /// <see cref="int"/>.MaxValue for callers that need to allocate a contiguous buffer.
    /// </summary>
    public static long EstimateSize(Snapshot snapshot) =>
        Math.Min(2.GiB, snapshot.EstimateMemory() + 1.KiB);

    private static void WriteMetadataColumn<TWriter>(ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Metadata keys must be in sorted order (ASCII): "from_block" < "from_hash" < "to_block" < "to_hash" < "version"
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> inner = new(ref innerWriter, expectedKeyCount: 5);

        Span<byte> blockNumBytes = stackalloc byte[8];

        BitConverter.TryWriteBytes(blockNumBytes, snapshot.From.BlockNumber);
        inner.Add("from_block"u8, blockNumBytes);

        inner.Add("from_hash"u8, snapshot.From.StateRoot.Bytes);

        BitConverter.TryWriteBytes(blockNumBytes, snapshot.To.BlockNumber);
        inner.Add("to_block"u8, blockNumBytes);

        inner.Add("to_hash"u8, snapshot.To.StateRoot.Bytes);

        inner.Add("version"u8, [0x01]);

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshot.MetadataTag);
    }

    private static void WriteAccountColumn<TWriter>(
        ref HsstDenseByteIndexBuilder<TWriter> outer, Snapshot snapshot,
        ArrayPoolList<((Address Addr, UInt256 Slot) Key, SlotValue? Value)> sortedStorages,
        ArrayPoolList<Address> uniqueAddresses,
        ArrayPoolList<ValueHash256> uniqueAddressHashes,
        ArrayPoolList<((Hash256 Addr, TreePath Path) Key, TrieNode Node)> storCompact,
        ArrayPoolList<((Hash256 Addr, TreePath Path) Key, TrieNode Node)> storFallback,
        BloomFilter? bloom = null,
        BloomFilter? trieBloom = null) where TWriter : IByteBufferWriter
    {
        const int slotPrefixLength = 31;

        // Address-level HSST keyed by 20-byte address-hash prefix.
        ref TWriter addressWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> addressLevel = new(ref addressWriter, new HsstBTreeOptions
        {
            MinSeparatorLength = 4,
        }, expectedKeyCount: uniqueAddresses.Count);
        byte[] rlpBuffer = new byte[256];
        RlpStream rlpStream = new(rlpBuffer);
        Span<byte> slotKey = stackalloc byte[32];
        Span<byte> currentPrefixBuf = stackalloc byte[slotPrefixLength];
        Span<byte> compactPathKey = stackalloc byte[8];
        Span<byte> fallbackPathKey = stackalloc byte[33];
        int storageIdx = 0;
        int storCompactIdx = 0;
        int storFallbackIdx = 0;

        for (int addrIdx = 0; addrIdx < uniqueAddresses.Count; addrIdx++)
        {
            // address may be null when this column key was contributed only by storage-
            // trie nodes (Hash256 → TrieNode). In that case slots/account/SD lookups are
            // skipped because all three are keyed by raw Address.
            Address? address = uniqueAddresses[addrIdx];
            ValueHash256 addressHash = uniqueAddressHashes[addrIdx];
            Hash256 addressHashCommit = addressHash.ToCommitment();
            ReadOnlySpan<byte> addressHashPrefix = addressHash.Bytes[..StorageHashPrefixLength];

            ulong addrBloomKey = 0;
            if (bloom is not null)
            {
                addrBloomKey = PersistedSnapshotBloomBuilder.AddressKey(addressHashCommit);
                bloom.Add(addrBloomKey);
            }

            // Begin per-address HSST. Up to 5 sub-tags 0x01..0x05; DenseByteIndex addresses
            // entries by tag-byte directly and gap-fills missing positions with length-0
            // values. Sub-tag value-presence semantics:
            //   0x01 storage compact: nested HSST(8-byte path → RLP)
            //   0x02 storage fallback: nested HSST(33-byte path → RLP)
            //   0x03 slots: nested HSST(SlotPrefix(31) → ByteTagMap)
            //   0x04 account: [] absent / [0x00] deleted / RLP-bytes present
            //   0x05 SD: [] absent / [0x00] destructed / [0x01] new account
            ref TWriter perAddrWriter = ref addressLevel.BeginValueWrite();
            using HsstDenseByteIndexBuilder<TWriter> perAddr = new(ref perAddrWriter);

            // Sub-tag 0x01: Storage trie nodes (compact, 8-byte path keys). Storage-trie
            // partitions are pre-sorted by address-hash prefix and path so a single advance
            // through storCompact / storFallback covers the run for this address-hash.
            int compactStart = storCompactIdx;
            while (storCompactIdx < storCompact.Count &&
                storCompact[storCompactIdx].Key.Addr.Bytes[..StorageHashPrefixLength].SequenceEqual(addressHashPrefix))
                storCompactIdx++;
            if (compactStart < storCompactIdx)
            {
                ref TWriter compactWriter = ref perAddr.BeginValueWrite();
                using HsstBuilder<TWriter> compactLevel = new(ref compactWriter, new HsstBTreeOptions { MinSeparatorLength = 8 },
                    expectedKeyCount: storCompactIdx - compactStart);
                for (int i = compactStart; i < storCompactIdx; i++)
                {
                    ((Hash256 _, TreePath path) k, TrieNode node) = storCompact[i];
                    k.path.EncodeWith8Byte(compactPathKey);
                    compactLevel.Add(compactPathKey, node.FullRlp.AsSpan());
                    trieBloom?.Add(PersistedSnapshotBloomBuilder.StorageNodeKey(addressHashCommit, in k.path));
                }
                compactLevel.Build();
                perAddr.FinishValueWrite(PersistedSnapshot.StorageCompactSubTag);
            }

            // Sub-tag 0x02: Storage trie nodes (fallback, 33-byte path keys).
            int fallbackStart = storFallbackIdx;
            while (storFallbackIdx < storFallback.Count &&
                storFallback[storFallbackIdx].Key.Addr.Bytes[..StorageHashPrefixLength].SequenceEqual(addressHashPrefix))
                storFallbackIdx++;
            if (fallbackStart < storFallbackIdx)
            {
                ref TWriter fbWriter = ref perAddr.BeginValueWrite();
                using HsstBuilder<TWriter> fbLevel = new(ref fbWriter, expectedKeyCount: storFallbackIdx - fallbackStart);
                for (int i = fallbackStart; i < storFallbackIdx; i++)
                {
                    ((Hash256 _, TreePath path) k, TrieNode node) = storFallback[i];
                    k.path.Path.Bytes.CopyTo(fallbackPathKey);
                    fallbackPathKey[32] = (byte)k.path.Length;
                    fbLevel.Add(fallbackPathKey, node.FullRlp.AsSpan());
                    trieBloom?.Add(PersistedSnapshotBloomBuilder.StorageNodeKey(addressHashCommit, in k.path));
                }
                fbLevel.Build();
                perAddr.FinishValueWrite(PersistedSnapshot.StorageFallbackSubTag);
            }

            // Sub-tag 0x03: Slots — skipped when no Address is known for this hash key.
            bool hasStorage = address is not null && storageIdx < sortedStorages.Count &&
                sortedStorages[storageIdx].Key.Addr.Bytes.SequenceEqual(address.Bytes);
            if (hasStorage)
            {
                ref TWriter slotWriter = ref perAddr.BeginValueWrite();
                using HsstBuilder<TWriter> prefixLevel = new(ref slotWriter, new HsstBTreeOptions { MinSeparatorLength = 4 });

                while (storageIdx < sortedStorages.Count &&
                    sortedStorages[storageIdx].Key.Addr.Bytes.SequenceEqual(address!.Bytes))
                {
                    sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                    slotKey[..slotPrefixLength].CopyTo(currentPrefixBuf);
                    ReadOnlySpan<byte> currentPrefix = currentPrefixBuf;

                    ref TWriter suffixWriter = ref prefixLevel.BeginValueWrite();
                    using HsstByteTagMapBuilder<TWriter> suffixLevel = new(ref suffixWriter);

                    while (storageIdx < sortedStorages.Count &&
                        sortedStorages[storageIdx].Key.Addr.Bytes.SequenceEqual(address.Bytes))
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

            // Sub-tag 0x04: Account. Present-marker encoding: [0x00] deleted, RLP-bytes
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

            // Sub-tag 0x05: Self-destruct. Present-marker encoding: [0x00] destructed,
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

    private static void WriteStateTopNodesColumn<TWriter>(ref HsstDenseByteIndexBuilder<TWriter> outer, ArrayPoolList<(TreePath Path, TrieNode Node)> stateNodes, BloomFilter? trieBloom = null) where TWriter : IByteBufferWriter
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> inner = new(ref innerWriter, new HsstBTreeOptions
        {
            MinSeparatorLength = 3,
        }, expectedKeyCount: stateNodes.Count);
        Span<byte> keyBuffer = stackalloc byte[3];
        foreach ((TreePath path, TrieNode node) in stateNodes)
        {
            path.EncodeWith3Byte(keyBuffer);
            inner.Add(keyBuffer, node.FullRlp.AsSpan());
            trieBloom?.Add(PersistedSnapshotBloomBuilder.StatePathKey(in path));
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshot.StateTopNodesTag);
    }

    private static void WriteStateNodesColumnCompact<TWriter>(ref HsstDenseByteIndexBuilder<TWriter> outer, ArrayPoolList<(TreePath Path, TrieNode Node)> stateNodes, BloomFilter? trieBloom = null) where TWriter : IByteBufferWriter
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> inner = new(ref innerWriter, new HsstBTreeOptions
        {
            MinSeparatorLength = 8,
        }, expectedKeyCount: stateNodes.Count);
        Span<byte> keyBuffer = stackalloc byte[8];
        foreach ((TreePath path, TrieNode node) in stateNodes)
        {
            path.EncodeWith8Byte(keyBuffer);
            inner.Add(keyBuffer, node.FullRlp.AsSpan());
            trieBloom?.Add(PersistedSnapshotBloomBuilder.StatePathKey(in path));
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshot.StateNodeTag);
    }

    private static void WriteStateNodesColumnFallback<TWriter>(ref HsstDenseByteIndexBuilder<TWriter> outer, ArrayPoolList<(TreePath Path, TrieNode Node)> stateNodes, BloomFilter? trieBloom = null) where TWriter : IByteBufferWriter
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> inner = new(ref innerWriter, expectedKeyCount: stateNodes.Count);
        Span<byte> keyBuffer = stackalloc byte[33];
        foreach ((TreePath path, TrieNode node) in stateNodes)
        {
            path.Path.Bytes.CopyTo(keyBuffer);
            keyBuffer[32] = (byte)path.Length;
            inner.Add(keyBuffer, node.FullRlp.AsSpan());
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
    internal static void ConvertFullToLinked<TWriter>(PersistedSnapshot fullSnapshot, ref TWriter writer) where TWriter : IByteBufferWriter
    {
        using WholeReadSession session = fullSnapshot.BeginWholeReadSession();
        WholeReadSessionReader r = session.GetReader();
        using HsstDenseByteIndexBuilder<TWriter> outerBuilder = new(ref writer);

        int snapshotId = fullSnapshot.Id;

        foreach (byte[] tag in s_columnTags)
        {
            if (!TryGetBound<WholeReadSessionReader, NoOpPin>(in r, new Bound(0, r.Length), tag, out long colOff, out long colLen))
                continue;
            // NodeRef encodes the offset as int; columnOffset must fit even though the
            // snapshot itself can exceed 2 GiB. Checked cast surfaces invariant violations.
            int columnOffset = checked((int)colOff);
            using NoOpPin colPin = r.PinBuffer(colOff, colLen);
            ReadOnlySpan<byte> column = colPin.Buffer;

            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();

            switch (tag[0])
            {
                // Metadata: copy as-is
                case 0x00:
                    CopyColumn(column, ref valueWriter);
                    break;
                // Per-address unified column: storage-trie sub-tags 0x01/0x02 get
                // their innermost path→RLP values replaced with NodeRefs; the slots /
                // account / SD sub-tags are small and remain inline.
                case 0x01:
                    ConvertAccountColumnToNodeRefs(column, columnOffset, ref valueWriter, snapshotId);
                    break;
                // Flat trie columns: convert values to NodeRefs (PackedArray, key sizes match column build sites)
                case 0x03:
                    ConvertFlatColumnToNodeRefs(column, ref valueWriter, snapshotId, columnOffset, keySize: 8);
                    break;
                case 0x05:
                    ConvertFlatColumnToNodeRefs(column, ref valueWriter, snapshotId, columnOffset, keySize: 3);
                    break;
                case 0x06:
                    ConvertFlatColumnToNodeRefs(column, ref valueWriter, snapshotId, columnOffset, keySize: 33);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown tag 0x{tag[0]:X2}");
            }

            outerBuilder.FinishValueWrite(tag);
        }

        outerBuilder.Build();
    }

    private static void CopyColumn<TWriter>(ReadOnlySpan<byte> column, ref TWriter writer) where TWriter : IByteBufferWriter =>
        IByteBufferWriter.Copy(ref writer, column);

    /// <summary>
    /// Convert a flat (non-nested) trie column's values to NodeRefs.
    /// Each entry's RLP value is replaced with a NodeRef pointing back to the Full snapshot.
    /// </summary>
    private static void ConvertFlatColumnToNodeRefs<TWriter>(
        ReadOnlySpan<byte> column, ref TWriter writer,
        int snapshotId, int columnOffset,
        int keySize) where TWriter : IByteBufferWriter
    {
        SpanByteReader reader = new(column);
        HsstPackedArrayBuilder<TWriter> builder = new(ref writer, keySize, NodeRef.Size);
        using HsstEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, column.Length));
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];

        while (e.MoveNext())
        {
            KeyValueEntry cur = e.Current;
            // NodeRef points directly at the RLP start; length is recovered from the
            // RLP header on read, so the referenced index doesn't need length metadata.
            NodeRef.Write(refBytes, new NodeRef(snapshotId, columnOffset + (int)cur.ValueBound.Offset));
            builder.Add(column.Slice((int)cur.KeyBound.Offset, checked((int)cur.KeyBound.Length)), refBytes);
        }

        builder.Build();
        builder.Dispose();
    }

    /// <summary>
    /// Convert a nested trie column (storage nodes) to NodeRefs.
    /// Outer keys (address hash prefixes) are preserved. Inner values are replaced with NodeRefs.
    /// </summary>
    private static void ConvertNestedColumnToNodeRefs<TWriter>(
        ReadOnlySpan<byte> column, int columnOffsetInSnapshot, ref TWriter writer,
        int snapshotId,
        int outerMinSep = 0, int innerKeySize = 0) where TWriter : IByteBufferWriter
    {
        SpanByteReader reader = new(column);
        HsstBuilder<TWriter> builder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = outerMinSep });
        using HsstEnumerator<SpanByteReader, NoOpPin> outerEnum = new(in reader, new Bound(0, column.Length));
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];

        while (outerEnum.MoveNext())
        {
            Bound innerScope = outerEnum.Current.ValueBound;

            ref TWriter innerWriter = ref builder.BeginValueWrite();
            HsstPackedArrayBuilder<TWriter> innerBuilder = new(ref innerWriter, innerKeySize, NodeRef.Size);
            using HsstEnumerator<SpanByteReader, NoOpPin> innerEnum = new(in reader, innerScope);

            while (innerEnum.MoveNext())
            {
                KeyValueEntry inner = innerEnum.Current;
                // NodeRef points directly at the RLP start (absolute snapshot offset).
                NodeRef.Write(refBytes, new NodeRef(snapshotId, columnOffsetInSnapshot + (int)inner.ValueBound.Offset));
                innerBuilder.Add(column.Slice((int)inner.KeyBound.Offset, checked((int)inner.KeyBound.Length)), refBytes);
            }

            innerBuilder.Build();
            innerBuilder.Dispose();
            builder.FinishValueWrite(column.Slice((int)outerEnum.Current.KeyBound.Offset, checked((int)outerEnum.Current.KeyBound.Length)));
        }

        builder.Build();
        builder.Dispose();
    }

    /// <summary>
    /// Convert column 0x01 (per-address) for a Full→Linked rewrite. Outer (BTree on
    /// 20-byte address-hash prefix) and inner DenseByteIndex layouts are preserved;
    /// only the storage-trie sub-tags (0x01 compact, 0x02 fallback) have their inner
    /// HSST values rewritten as NodeRefs pointing back into the source Full snapshot's
    /// column 0x01 region. Sub-tags 0x03 (slots) / 0x04 (account RLP) / 0x05 (SD) are
    /// copied as-is — they're small inline values and aren't shared across snapshots.
    /// </summary>
    private static void ConvertAccountColumnToNodeRefs<TWriter>(
        ReadOnlySpan<byte> column, int columnOffsetInSnapshot, ref TWriter writer,
        int snapshotId) where TWriter : IByteBufferWriter
    {
        SpanByteReader reader = new(column);
        using HsstBuilder<TWriter> outerBuilder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = 4 });
        using HsstEnumerator<SpanByteReader, NoOpPin> outerEnum = new(in reader, new Bound(0, column.Length));

        while (outerEnum.MoveNext())
        {
            Bound perAddrScope = outerEnum.Current.ValueBound;
            int perAddrOffInColumn = checked((int)perAddrScope.Offset);
            int perAddrLen = checked((int)perAddrScope.Length);
            ReadOnlySpan<byte> perAddrSpan = column.Slice(perAddrOffInColumn, perAddrLen);

            ref TWriter perAddrWriter = ref outerBuilder.BeginValueWrite();
            using HsstDenseByteIndexBuilder<TWriter> perAddrBuilder = new(ref perAddrWriter);

            // Sub-tag 0x01: storage trie compact. Inner HSST values become NodeRefs.
            if (TryGetBound(perAddrSpan, PersistedSnapshot.StorageCompactSubTag, out int subOff, out int subLen) && subLen > 0)
            {
                ref TWriter subWriter = ref perAddrBuilder.BeginValueWrite();
                ConvertStorageTrieSubTagToNodeRefs(
                    column, perAddrOffInColumn + subOff, subLen, columnOffsetInSnapshot,
                    ref subWriter, snapshotId, innerKeySize: 8);
                perAddrBuilder.FinishValueWrite(PersistedSnapshot.StorageCompactSubTag);
            }

            // Sub-tag 0x02: storage trie fallback. Same conversion, 33-byte path keys.
            if (TryGetBound(perAddrSpan, PersistedSnapshot.StorageFallbackSubTag, out subOff, out subLen) && subLen > 0)
            {
                ref TWriter subWriter = ref perAddrBuilder.BeginValueWrite();
                ConvertStorageTrieSubTagToNodeRefs(
                    column, perAddrOffInColumn + subOff, subLen, columnOffsetInSnapshot,
                    ref subWriter, snapshotId, innerKeySize: 33);
                perAddrBuilder.FinishValueWrite(PersistedSnapshot.StorageFallbackSubTag);
            }

            // Sub-tag 0x03: slots — copy bytes as-is. Slot values are inline, not NodeRefs.
            if (TryGetBound(perAddrSpan, PersistedSnapshot.SlotSubTag, out subOff, out subLen) && subLen > 0)
                perAddrBuilder.Add(PersistedSnapshot.SlotSubTag, perAddrSpan.Slice(subOff, subLen));

            // Sub-tag 0x04: account RLP — inline.
            if (TryGetBound(perAddrSpan, PersistedSnapshot.AccountSubTag, out subOff, out subLen) && subLen > 0)
                perAddrBuilder.Add(PersistedSnapshot.AccountSubTag, perAddrSpan.Slice(subOff, subLen));

            // Sub-tag 0x05: self-destruct flag — inline.
            if (TryGetBound(perAddrSpan, PersistedSnapshot.SelfDestructSubTag, out subOff, out subLen) && subLen > 0)
                perAddrBuilder.Add(PersistedSnapshot.SelfDestructSubTag, perAddrSpan.Slice(subOff, subLen));

            perAddrBuilder.Build();
            Bound keyBound = outerEnum.Current.KeyBound;
            outerBuilder.FinishValueWrite(column.Slice(checked((int)keyBound.Offset), checked((int)keyBound.Length)));
        }

        outerBuilder.Build();
    }

    private static void ConvertStorageTrieSubTagToNodeRefs<TWriter>(
        ReadOnlySpan<byte> column, int subTagOffInColumn, int subTagLen,
        int columnOffsetInSnapshot,
        ref TWriter writer, int snapshotId, int innerKeySize) where TWriter : IByteBufferWriter
    {
        SpanByteReader reader = new(column);
        // The sub-tag value is itself an inner HSST(BTree) of (path → RLP). Walk every
        // entry, replacing RLP with a NodeRef whose ValueLengthOffset is the
        // snapshot-absolute offset of the LEB128 length cursor in the source Full
        // snapshot's column 0x01 region (matching the convention used by the flat /
        // nested converters above).
        HsstPackedArrayBuilder<TWriter> innerBuilder = new(ref writer, innerKeySize, NodeRef.Size);
        using HsstEnumerator<SpanByteReader, NoOpPin> innerEnum = new(in reader, new Bound(subTagOffInColumn, subTagLen));
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];

        while (innerEnum.MoveNext())
        {
            KeyValueEntry inner = innerEnum.Current;
            int metaStartInColumn = (int)(inner.ValueBound.Offset + inner.ValueBound.Length);
            NodeRef.Write(refBytes, new NodeRef(snapshotId, columnOffsetInSnapshot + metaStartInColumn));
            innerBuilder.Add(column.Slice((int)inner.KeyBound.Offset, checked((int)inner.KeyBound.Length)), refBytes);
        }

        innerBuilder.Build();
        innerBuilder.Dispose();
    }

    /// <summary>
    /// N-way merge of N persisted snapshots (oldest-first) into output buffer.
    /// Pre-converts all Full snapshots to Linked so the merge only handles Linked snapshots
    /// (all trie values are already NodeRefs). This eliminates the dual code path in trie merges.
    /// </summary>
    internal static void NWayMergeSnapshots<TWriter>(PersistedSnapshotList snapshots, ref TWriter writer, HashSet<int> referencedIds, BloomFilter? bloom = null) where TWriter : IByteBufferWriter
    {
        int n = snapshots.Count;

        // Pre-convert Full snapshots to Linked using a temporary MemoryArenaManager
        using MemoryArenaManager tempArena = new(1024 * 1024);
        PersistedSnapshotList mergeSnapshots = new(n);

        try
        {
            for (int i = 0; i < n; i++)
            {
                if (snapshots[i].Type == PersistedSnapshotType.Full)
                {
                    long estimatedSize = snapshots[i].Size / 2 + 4096;
                    using ArenaWriter tempWriter = tempArena.CreateWriter(Math.Max(estimatedSize, snapshots[i].Size), ArenaReservationTags.TempLinkedConversion);
                    ConvertFullToLinked(snapshots[i], ref tempWriter.GetWriter());
                    (_, ArenaReservation tempRes) = tempWriter.Complete();
                    PersistedSnapshot convertedSnap = new(snapshots[i].Id, snapshots[i].From, snapshots[i].To,
                        PersistedSnapshotType.Linked, tempRes);
                    tempRes.Dispose();
                    mergeSnapshots.Add(convertedSnap);
                }
                else
                {
                    if (!snapshots[i].TryAcquire())
                        throw new InvalidOperationException("Cannot acquire lease for snapshot");
                    mergeSnapshots.Add(snapshots[i]);
                }
            }

            using HsstDenseByteIndexBuilder<TWriter> outerBuilder = new(ref writer);

            foreach (byte[] tag in s_columnTags)
            {
                ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();

                // All trie columns now use NWayStreamingMerge since all inputs are Linked (values are NodeRefs)
                switch (tag[0])
                {
                    case 0x00:
                        NWayMetadataMerge(snapshots, ref valueWriter, referencedIds);
                        break;
                    case 0x01:
                        NWayMergeAccountColumn(mergeSnapshots, tag, ref valueWriter, bloom);
                        break;
                    case 0x03:
                        NWayStreamingMerge(mergeSnapshots, tag, ref valueWriter, keySize: 8);
                        break;
                    case 0x05:
                        NWayStreamingMerge(mergeSnapshots, tag, ref valueWriter, keySize: 3);
                        break;
                    case 0x06:
                        NWayStreamingMerge(mergeSnapshots, tag, ref valueWriter, keySize: 33);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown tag 0x{tag[0]:X2}");
                }

                outerBuilder.FinishValueWrite(tag);
            }

            outerBuilder.Build();
        }
        finally
        {
            mergeSnapshots.Dispose();
        }
    }

    private static int SpanOffset(ReadOnlySpan<byte> outer, ReadOnlySpan<byte> inner) =>
        inner.IsEmpty ? 0 : (int)Unsafe.ByteOffset(
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(outer)),
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(inner)));

    // --- N-Way merge methods ---

    /// <summary>
    /// N-way streaming merge of a column across N snapshots. On key collision, newest (highest index) wins.
    /// Uses <see cref="HsstMergeEnumerator"/> for zero-allocation cursor-based enumeration.
    /// </summary>
    internal static void NWayStreamingMerge<TWriter>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer,
        int keySize) where TWriter : IByteBufferWriter
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstMergeEnumerator> enums = new(n, n);
        using ArrayPoolList<bool> hasMore = new(n, n);
        using ArrayPoolList<(long Offset, long Length)> columnBounds = new(n, n);
        using ArrayPoolList<WholeReadSession> sessions = new(n, n);

        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                WholeReadSessionReader r = sessions[i].GetReader();
                columnBounds[i] = TryGetBound<WholeReadSessionReader, NoOpPin>(in r, new Bound(0, r.Length), tag, out long colOff, out long colLen)
                    ? (colOff, colLen) : (0, 0);
                enums[i] = new HsstMergeEnumerator(in r, new Bound(columnBounds[i].Offset, columnBounds[i].Length));
                hasMore[i] = enums[i].MoveNext(in r);
            }

            using HsstPackedArrayBuilder<TWriter> builder = new(ref writer, keySize, NodeRef.Size);

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
                    Bound bI = enums[i].CurrentKey;
                    Bound bM = enums[minIdx].CurrentKey;
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    WholeReadSessionReader rM = sessions[minIdx].GetReader();
                    using NoOpPin pinI = rI.PinBuffer(bI.Offset, bI.Length);
                    using NoOpPin pinM = rM.PinBuffer(bM.Offset, bM.Length);
                    int cmp = pinI.Buffer.SequenceCompareTo(pinM.Buffer);
                    if (cmp < 0) minIdx = i;
                    else if (cmp == 0) minIdx = i; // newer (higher index) wins
                }

                if (minIdx < 0) break;

                Bound keyBound = enums[minIdx].CurrentKey;
                Bound valBound = enums[minIdx].CurrentValue;
                WholeReadSessionReader minIdxReader = sessions[minIdx].GetReader();
                using NoOpPin keyPin = minIdxReader.PinBuffer(keyBound.Offset, keyBound.Length);
                using NoOpPin valPin = minIdxReader.PinBuffer(valBound.Offset, valBound.Length);
                ReadOnlySpan<byte> minKey = keyPin.Buffer;
                builder.Add(minKey, valPin.Buffer);

                for (int i = 0; i < n; i++)
                {
                    if (i == minIdx || !hasMore[i]) continue;
                    Bound bI = enums[i].CurrentKey;
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    using NoOpPin pinI = rI.PinBuffer(bI.Offset, bI.Length);
                    if (pinI.Buffer.SequenceCompareTo(minKey) == 0)
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
            for (int i = 0; i < n; i++) enums[i]?.Dispose();
            for (int i = 0; i < n; i++) sessions[i]?.Dispose();
        }
    }

    /// <summary>
    /// N-way nested streaming merge: outer keys merged across N sources,
    /// when M sources share an outer key their inner HSST values are merged via NWayStreamingMerge.
    /// Single-source keys are copied as-is.
    /// </summary>
    internal static void NWayNestedStreamingMerge<TWriter>(
        HsstMergeEnumerator[] enums, bool[] hasMore, int n,
        WholeReadSession[] sessions,
        ref TWriter writer,
        int outerMinSep = 0, int innerMinSep = 0,
        bool innerByteTagMap = false) where TWriter : IByteBufferWriter
    {
        using HsstBuilder<TWriter> builder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = outerMinSep });

        // Temp list for collecting matching source indices
        using ArrayPoolList<int> matchingSourcesList = new(n, n);
        int[] matchingSources = matchingSourcesList.UnsafeGetInternalArray();

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
                Bound bI = enums[i].CurrentKey;
                Bound bM = enums[minIdx].CurrentKey;
                WholeReadSessionReader rI = sessions[i].GetReader();
                WholeReadSessionReader rM = sessions[minIdx].GetReader();
                using NoOpPin pinI = rI.PinBuffer(bI.Offset, bI.Length);
                using NoOpPin pinM = rM.PinBuffer(bM.Offset, bM.Length);
                int cmp = pinI.Buffer.SequenceCompareTo(pinM.Buffer);
                if (cmp < 0) minIdx = i;
            }

            if (minIdx < 0) break;

            Bound minKeyBound = enums[minIdx].CurrentKey;
            WholeReadSessionReader minIdxReader = sessions[minIdx].GetReader();
            using NoOpPin minKeyPin = minIdxReader.PinBuffer(minKeyBound.Offset, minKeyBound.Length);
            ReadOnlySpan<byte> minKey = minKeyPin.Buffer;

            // Collect all sources with this key
            int matchCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (!hasMore[i]) continue;
                Bound bI = enums[i].CurrentKey;
                WholeReadSessionReader rI = sessions[i].GetReader();
                using NoOpPin pinI = rI.PinBuffer(bI.Offset, bI.Length);
                if (pinI.Buffer.SequenceCompareTo(minKey) == 0)
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
                NWayInnerMerge(enums, matchingSources, matchCount, sessions,
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
    private static void NWayInnerMerge<TWriter>(
        HsstMergeEnumerator[] outerEnums, int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        ref TWriter writer,
        int minSeparatorLength = 0,
        bool useByteTagMap = false) where TWriter : IByteBufferWriter
    {
        using ArrayPoolList<HsstMergeEnumerator> innerEnums = new(matchCount, matchCount);
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
                innerEnums[j] = new HsstMergeEnumerator(in r, new Bound(innerBounds[j].Offset, innerBounds[j].Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
            }

            if (useByteTagMap)
                MergeIntoByteTagMap(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions, ref writer);
            else
                MergeIntoBTree(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions, ref writer, minSeparatorLength);
        }
        finally
        {
            for (int j = 0; j < matchCount; j++) innerEnums[j]?.Dispose();
        }
    }

    private static int PickMinIdx(ArrayPoolList<HsstMergeEnumerator> innerEnums, ArrayPoolList<bool> innerHasMore, ArrayPoolList<(long Offset, long Length)> innerBounds, int[] matchingSources, int matchCount, WholeReadSession[] sessions)
    {
        int minIdx = -1;
        for (int j = 0; j < matchCount; j++)
        {
            if (!innerHasMore[j]) continue;
            if (minIdx < 0) { minIdx = j; continue; }
            Bound bJ = innerEnums[j].CurrentKey;
            Bound bM = innerEnums[minIdx].CurrentKey;
            WholeReadSessionReader rJ = sessions[matchingSources[j]].GetReader();
            WholeReadSessionReader rM = sessions[matchingSources[minIdx]].GetReader();
            using NoOpPin pinJ = rJ.PinBuffer(bJ.Offset, bJ.Length);
            using NoOpPin pinM = rM.PinBuffer(bM.Offset, bM.Length);
            int cmp = pinJ.Buffer.SequenceCompareTo(pinM.Buffer);
            if (cmp < 0) minIdx = j;
            else if (cmp == 0) minIdx = j; // newer (higher j = higher source index) wins
        }
        return minIdx;
    }

    private static void AdvanceMatching(ArrayPoolList<HsstMergeEnumerator> innerEnums, ArrayPoolList<bool> innerHasMore, ArrayPoolList<(long Offset, long Length)> innerBounds, int[] matchingSources, int matchCount, WholeReadSession[] sessions, int minIdx, ReadOnlySpan<byte> minKey)
    {
        for (int j = 0; j < matchCount; j++)
        {
            if (j == minIdx || !innerHasMore[j]) continue;
            Bound jKey = innerEnums[j].CurrentKey;
            WholeReadSessionReader rJ = sessions[matchingSources[j]].GetReader();
            using NoOpPin pinJ = rJ.PinBuffer(jKey.Offset, jKey.Length);
            if (pinJ.Buffer.SequenceCompareTo(minKey) == 0)
                innerHasMore[j] = innerEnums[j].MoveNext(in rJ);
        }
        WholeReadSessionReader rMin = sessions[matchingSources[minIdx]].GetReader();
        innerHasMore[minIdx] = innerEnums[minIdx].MoveNext(in rMin);
    }

    private static void MergeIntoBTree<TWriter>(
        ArrayPoolList<HsstMergeEnumerator> innerEnums, ArrayPoolList<bool> innerHasMore,
        ArrayPoolList<(long Offset, long Length)> innerBounds,
        int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        ref TWriter writer, int minSeparatorLength) where TWriter : IByteBufferWriter
    {
        using HsstBuilder<TWriter> builder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = minSeparatorLength });
        while (true)
        {
            int minIdx = PickMinIdx(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions);
            if (minIdx < 0) break;

            Bound kb = innerEnums[minIdx].CurrentKey;
            Bound vb = innerEnums[minIdx].CurrentValue;
            WholeReadSessionReader r = sessions[matchingSources[minIdx]].GetReader();
            using NoOpPin keyPin = r.PinBuffer(kb.Offset, kb.Length);
            using NoOpPin valPin = r.PinBuffer(vb.Offset, vb.Length);
            ReadOnlySpan<byte> minKey = keyPin.Buffer;
            builder.Add(minKey, valPin.Buffer);
            AdvanceMatching(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions, minIdx, minKey);
        }
        builder.Build();
    }

    private static void MergeIntoByteTagMap<TWriter>(
        ArrayPoolList<HsstMergeEnumerator> innerEnums, ArrayPoolList<bool> innerHasMore,
        ArrayPoolList<(long Offset, long Length)> innerBounds,
        int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        ref TWriter writer) where TWriter : IByteBufferWriter
    {
        using HsstByteTagMapBuilder<TWriter> builder = new(ref writer);
        while (true)
        {
            int minIdx = PickMinIdx(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions);
            if (minIdx < 0) break;

            Bound kb = innerEnums[minIdx].CurrentKey;
            Bound vb = innerEnums[minIdx].CurrentValue;
            WholeReadSessionReader r = sessions[matchingSources[minIdx]].GetReader();
            using NoOpPin keyPin = r.PinBuffer(kb.Offset, kb.Length);
            using NoOpPin valPin = r.PinBuffer(vb.Offset, vb.Length);
            ReadOnlySpan<byte> minKey = keyPin.Buffer;
            builder.Add(minKey[0], valPin.Buffer);
            AdvanceMatching(innerEnums, innerHasMore, innerBounds, matchingSources, matchCount, sessions, minIdx, minKey);
        }
        builder.Build();
    }

    /// <summary>
    /// N-way nested streaming merge across N persisted snapshots.
    /// Initializes enumerators from snapshot data and delegates to the core merge method.
    /// </summary>
    internal static void NWayNestedStreamingMerge<TWriter>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer,
        int outerMinSep = 0, int innerMinSep = 0) where TWriter : IByteBufferWriter
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstMergeEnumerator> enumsList = new(n, n);
        using ArrayPoolList<bool> hasMoreList = new(n, n);
        using ArrayPoolList<(long Offset, long Length)> columnBoundsList = new(n, n);
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        HsstMergeEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        bool[] hasMore = hasMoreList.UnsafeGetInternalArray();
        (long Offset, long Length)[] columnBounds = columnBoundsList.UnsafeGetInternalArray();
        WholeReadSession[] sessions = sessionsList.UnsafeGetInternalArray();

        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                WholeReadSessionReader r = sessions[i].GetReader();
                columnBounds[i] = TryGetBound<WholeReadSessionReader, NoOpPin>(in r, new Bound(0, r.Length), tag, out long colOff, out long colLen)
                    ? (colOff, colLen) : (0, 0);
                enums[i] = new HsstMergeEnumerator(in r, new Bound(columnBounds[i].Offset, columnBounds[i].Length));
                hasMore[i] = enums[i].MoveNext(in r);
            }

            NWayNestedStreamingMerge(enums, hasMore, n, sessions,
                ref writer, outerMinSep, innerMinSep);
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i]?.Dispose();
            for (int i = 0; i < n; i++) sessions[i]?.Dispose();
        }
    }

    /// <summary>
    /// Trie-specific nested streaming merge for storage trie columns (0x07/0x08). Outer
    /// (storage hash prefix) keeps the BTree layout; inner (TreePath -> NodeRef) is built
    /// as a fixed-size PackedArray since both inner key and value (NodeRef) are fixed.
    /// </summary>
    internal static void NWayNestedStreamingMergeTrie<TWriter>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer,
        int outerMinSep, int innerKeySize) where TWriter : IByteBufferWriter
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstMergeEnumerator> enumsList = new(n, n);
        using ArrayPoolList<bool> hasMoreList = new(n, n);
        using ArrayPoolList<(long Offset, long Length)> columnBoundsList = new(n, n);
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        using ArrayPoolList<int> matchingSourcesList = new(n, n);
        HsstMergeEnumerator[] enums = enumsList.UnsafeGetInternalArray();
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
                columnBounds[i] = TryGetBound<WholeReadSessionReader, NoOpPin>(in r, new Bound(0, r.Length), tag, out long colOff, out long colLen)
                    ? (colOff, colLen) : (0, 0);
                enums[i] = new HsstMergeEnumerator(in r, new Bound(columnBounds[i].Offset, columnBounds[i].Length));
                hasMore[i] = enums[i].MoveNext(in r);
            }

            using HsstBuilder<TWriter> outerBuilder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = outerMinSep });

            while (true)
            {
                int minIdx = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    if (minIdx < 0) { minIdx = i; continue; }
                    Bound bI = enums[i].CurrentKey;
                    Bound bM = enums[minIdx].CurrentKey;
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    WholeReadSessionReader rM = sessions[minIdx].GetReader();
                    using NoOpPin pinI = rI.PinBuffer(bI.Offset, bI.Length);
                    using NoOpPin pinM = rM.PinBuffer(bM.Offset, bM.Length);
                    int cmp = pinI.Buffer.SequenceCompareTo(pinM.Buffer);
                    if (cmp < 0) minIdx = i;
                }
                if (minIdx < 0) break;

                Bound minKeyBound = enums[minIdx].CurrentKey;
                WholeReadSessionReader minIdxReader = sessions[minIdx].GetReader();
                using NoOpPin minKeyPin = minIdxReader.PinBuffer(minKeyBound.Offset, minKeyBound.Length);
                ReadOnlySpan<byte> minKey = minKeyPin.Buffer;

                int matchCount = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    Bound bI = enums[i].CurrentKey;
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    using NoOpPin pinI = rI.PinBuffer(bI.Offset, bI.Length);
                    if (pinI.Buffer.SequenceCompareTo(minKey) == 0)
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
                    NWayInnerMergeTrie(enums, matchingSources, matchCount, sessions,
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
            for (int i = 0; i < n; i++) enums[i]?.Dispose();
            for (int i = 0; i < n; i++) sessions[i]?.Dispose();
        }
    }

    /// <summary>
    /// Trie-specific inner merge: M sources share an outer key; merge their inner trie HSSTs
    /// (TreePath -> NodeRef, fixed-size both sides) into a single PackedArray.
    /// </summary>
    private static void NWayInnerMergeTrie<TWriter>(
        HsstMergeEnumerator[] outerEnums, int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        ref TWriter writer,
        int keySize) where TWriter : IByteBufferWriter
    {
        using ArrayPoolList<HsstMergeEnumerator> innerEnums = new(matchCount, matchCount);
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
                innerEnums[j] = new HsstMergeEnumerator(in r, new Bound(innerBounds[j].Offset, innerBounds[j].Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
            }

            using HsstPackedArrayBuilder<TWriter> builder = new(ref writer, keySize, NodeRef.Size);

            while (true)
            {
                int minIdx = -1;
                for (int j = 0; j < matchCount; j++)
                {
                    if (!innerHasMore[j]) continue;
                    if (minIdx < 0) { minIdx = j; continue; }
                    Bound bJ = innerEnums[j].CurrentKey;
                    Bound bM = innerEnums[minIdx].CurrentKey;
                    WholeReadSessionReader rJ = sessions[matchingSources[j]].GetReader();
                    WholeReadSessionReader rM = sessions[matchingSources[minIdx]].GetReader();
                    using NoOpPin pinJ = rJ.PinBuffer(bJ.Offset, bJ.Length);
                    using NoOpPin pinM = rM.PinBuffer(bM.Offset, bM.Length);
                    int cmp = pinJ.Buffer.SequenceCompareTo(pinM.Buffer);
                    if (cmp < 0) minIdx = j;
                    else if (cmp == 0) minIdx = j; // newer wins
                }
                if (minIdx < 0) break;

                Bound kb = innerEnums[minIdx].CurrentKey;
                Bound vb2 = innerEnums[minIdx].CurrentValue;
                WholeReadSessionReader minReader = sessions[matchingSources[minIdx]].GetReader();
                using NoOpPin keyPin = minReader.PinBuffer(kb.Offset, kb.Length);
                using NoOpPin valPin = minReader.PinBuffer(vb2.Offset, vb2.Length);
                ReadOnlySpan<byte> minKey = keyPin.Buffer;
                builder.Add(minKey, valPin.Buffer);

                for (int j = 0; j < matchCount; j++)
                {
                    if (j == minIdx || !innerHasMore[j]) continue;
                    Bound jKey = innerEnums[j].CurrentKey;
                    WholeReadSessionReader jr = sessions[matchingSources[j]].GetReader();
                    using NoOpPin jPin = jr.PinBuffer(jKey.Offset, jKey.Length);
                    if (jPin.Buffer.SequenceCompareTo(minKey) == 0)
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
            for (int j = 0; j < matchCount; j++) innerEnums[j]?.Dispose();
        }
    }

    /// <summary>
    /// N-way merge of the account column (tag 0x01) across N snapshots.
    /// Outer: 20-byte address keys (minSep=4). For matching addresses with M sources,
    /// calls <see cref="NWayMergePerAddressHsst"/>. Single source: copy as-is.
    /// </summary>
    internal static void NWayMergeAccountColumn<TWriter>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer, BloomFilter? bloom = null) where TWriter : IByteBufferWriter
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstMergeEnumerator> enumsList = new(n, n);
        using ArrayPoolList<bool> hasMoreList = new(n, n);
        using ArrayPoolList<(long Offset, long Length)> columnBoundsList = new(n, n);
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        using ArrayPoolList<int> matchingSourcesList = new(n, n);
        HsstMergeEnumerator[] enums = enumsList.UnsafeGetInternalArray();
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
                columnBounds[i] = TryGetBound<WholeReadSessionReader, NoOpPin>(in r, new Bound(0, r.Length), tag, out long colOff, out long colLen)
                    ? (colOff, colLen) : (0, 0);
                enums[i] = new HsstMergeEnumerator(in r, new Bound(columnBounds[i].Offset, columnBounds[i].Length));
                hasMore[i] = enums[i].MoveNext(in r);
            }

            using HsstBuilder<TWriter> builder = new(ref writer, new HsstBTreeOptions { MinSeparatorLength = 4 });

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
                    Bound bI = enums[i].CurrentKey;
                    Bound bM = enums[minIdx].CurrentKey;
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    WholeReadSessionReader rM = sessions[minIdx].GetReader();
                    using NoOpPin pinI = rI.PinBuffer(bI.Offset, bI.Length);
                    using NoOpPin pinM = rM.PinBuffer(bM.Offset, bM.Length);
                    int cmp = pinI.Buffer.SequenceCompareTo(pinM.Buffer);
                    if (cmp < 0) minIdx = i;
                }

                if (minIdx < 0) break;

                Bound minKeyBound = enums[minIdx].CurrentKey;
                WholeReadSessionReader minIdxReader = sessions[minIdx].GetReader();
                using NoOpPin minKeyPin = minIdxReader.PinBuffer(minKeyBound.Offset, minKeyBound.Length);
                ReadOnlySpan<byte> minKey = minKeyPin.Buffer;

                int matchCount = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    Bound bI = enums[i].CurrentKey;
                    WholeReadSessionReader rI = sessions[i].GetReader();
                    using NoOpPin pinI = rI.PinBuffer(bI.Offset, bI.Length);
                    if (pinI.Buffer.SequenceCompareTo(minKey) == 0)
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
                        if (TryGet(perAddrHsst, PersistedSnapshot.SlotSubTag, out ReadOnlySpan<byte> slotSection))
                            AddSlotKeysToBloom(slotSection, addrKey, bloom);
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
                    NWayMergePerAddressHsst(
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
            for (int i = 0; i < n; i++) enums[i]?.Dispose();
            for (int i = 0; i < n; i++) sessions[i]?.Dispose();
        }
    }

    /// <summary>
    /// N-way merge of per-address HSSTs from M sources (oldest-first by matchingSources order).
    /// Sub-tags emitted in ascending byte order so the DenseByteIndex builder accepts them:
    /// - 0x01 StorageCompact: streaming merge of inner (8-byte path → NodeRef) PackedArrays.
    ///   No destruct barrier — orphan nodes are unreachable from the new storage root.
    /// - 0x02 StorageFallback: same as 0x01 with 33-byte path keys.
    /// - 0x03 Slots: find newest destruct barrier, merge slots from barrier..M-1 via nested streaming merge
    /// - 0x04 Account: newest wins (walk M-1..0, first with AccountSubTag)
    /// - 0x05 SelfDestruct: iterate 0..M-1, apply TryAdd semantics
    /// </summary>
    private static void NWayMergePerAddressHsst<TWriter>(
        HsstMergeEnumerator[] outerEnums, int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        ref TWriter writer, BloomFilter? bloom = null, ulong addrBloomKey = 0) where TWriter : IByteBufferWriter
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

        // Sub-tags 0x01 / 0x02: storage trie compact / fallback. Each source carries an
        // inner HSST keyed by encoded TreePath; values are NodeRefs (since NWayMerge
        // converts Full→Linked first). N-way streaming merge per sub-tag with newest-
        // wins on key collision; no destruct barrier since orphan nodes are unreachable
        // from the new storage root.
        MergeStorageTrieSubTag(matchingSources, matchCount, sessions, perAddrBounds,
            ref perAddrBuilder, PersistedSnapshot.StorageCompactSubTag, innerKeySize: 8);
        MergeStorageTrieSubTag(matchingSources, matchCount, sessions, perAddrBounds,
            ref perAddrBuilder, PersistedSnapshot.StorageFallbackSubTag, innerKeySize: 33);

        // Find newest destruct barrier: newest j where SelfDestructSubTag is present and
        // marks "destructed" ([0x00]). With DenseByteIndex per-address encoding, sub-tag
        // values are presence-marked: length 0 = absent, [0x00] = destructed, [0x01] = new.
        int destructBarrier = -1;
        for (int j = 0; j < matchCount; j++)
        {
            WholeReadSessionReader r = sessions[matchingSources[j]].GetReader();
            using NoOpPin perAddrPin = r.PinBuffer(perAddrBounds[j].Offset, perAddrBounds[j].Length);
            if (TryGet(perAddrPin.Buffer, PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> sdVal)
                && sdVal.Length == 1 && sdVal[0] == 0x00)
                destructBarrier = j;
        }

        // Sub-tag 0x01: Slots
        // Merge slots only from max(0, destructBarrier)..matchCount-1
        int slotStart = Math.Max(0, destructBarrier);

        if (bloom is not null)
        {
            for (int j = slotStart; j < matchCount; j++)
            {
                WholeReadSessionReader r = sessions[matchingSources[j]].GetReader();
                using NoOpPin perAddrPin = r.PinBuffer(perAddrBounds[j].Offset, perAddrBounds[j].Length);
                if (TryGet(perAddrPin.Buffer, PersistedSnapshot.SlotSubTag, out ReadOnlySpan<byte> slotSection))
                    AddSlotKeysToBloom(slotSection, addrBloomKey, bloom);
            }
        }
        {
            // Collect sources that have slots in the range
            int slotSourceCount = 0;
            int slotCapacity = matchCount - slotStart;
            using ArrayPoolList<int> slotSourcesList = new(slotCapacity, slotCapacity);
            using ArrayPoolList<(long Offset, long Length)> slotBoundsList = new(slotCapacity, slotCapacity);
            int[] slotSources = slotSourcesList.UnsafeGetInternalArray();
            (long Offset, long Length)[] slotBounds = slotBoundsList.UnsafeGetInternalArray();
            for (int j = slotStart; j < matchCount; j++)
            {
                WholeReadSessionReader r = sessions[matchingSources[j]].GetReader();
                if (TryGetBound<WholeReadSessionReader, NoOpPin>(in r, new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length), PersistedSnapshot.SlotSubTag, out long slotOff, out long slotLen))
                {
                    slotSources[slotSourceCount] = j;
                    // slotOff is reader-absolute (snapshot-absolute) since the scope was relative to the snapshot.
                    slotBounds[slotSourceCount] = (slotOff, slotLen);
                    slotSourceCount++;
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
                using ArrayPoolList<HsstMergeEnumerator> slotEnumsList = new(slotSourceCount, slotSourceCount);
                using ArrayPoolList<bool> slotHasMoreList = new(slotSourceCount, slotSourceCount);
                using ArrayPoolList<WholeReadSession> slotSessionsList = new(slotSourceCount, slotSourceCount);
                HsstMergeEnumerator[] slotEnums = slotEnumsList.UnsafeGetInternalArray();
                bool[] slotHasMore = slotHasMoreList.UnsafeGetInternalArray();
                WholeReadSession[] slotSessions = slotSessionsList.UnsafeGetInternalArray();
                try
                {
                    for (int j = 0; j < slotSourceCount; j++)
                    {
                        slotSessions[j] = sessions[matchingSources[slotSources[j]]];
                        WholeReadSessionReader slotReader = slotSessions[j].GetReader();
                        slotEnums[j] = new HsstMergeEnumerator(in slotReader, new Bound(slotBounds[j].Offset, slotBounds[j].Length));
                        slotHasMore[j] = slotEnums[j].MoveNext(in slotReader);
                    }

                    ref TWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                    NWayNestedStreamingMerge(
                        slotEnums, slotHasMore, slotSourceCount, slotSessions,
                        ref slotWriter,
                        outerMinSep: 4, innerByteTagMap: true);
                    perAddrBuilder.FinishValueWrite(PersistedSnapshot.SlotSubTag);
                }
                finally
                {
                    for (int j = 0; j < slotSourceCount; j++) slotEnums[j]?.Dispose();
                }
            }
        }

        // Sub-tag 0x04: Account — newest wins (walk M-1..0, first present (length>0)).
        {
            for (int j = matchCount - 1; j >= 0; j--)
            {
                WholeReadSessionReader r = sessions[matchingSources[j]].GetReader();
                using NoOpPin perAddrPin = r.PinBuffer(perAddrBounds[j].Offset, perAddrBounds[j].Length);
                if (TryGet(perAddrPin.Buffer, PersistedSnapshot.AccountSubTag, out ReadOnlySpan<byte> account) && account.Length > 0)
                {
                    perAddrBuilder.Add(PersistedSnapshot.AccountSubTag, account);
                    break;
                }
            }
        }

        // Sub-tag 0x05: SelfDestruct — iterate 0..M-1, apply TryAdd semantics. Presence
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
                if (!TryGetBound<WholeReadSessionReader, NoOpPin>(in r, new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length), PersistedSnapshot.SelfDestructSubTag, out long sdOff, out long sdLen) || sdLen == 0)
                    continue;

                if (sdSrcJ < 0)
                {
                    sdSrcJ = j;
                    sdValOff = sdOff;
                    sdValLen = sdLen;
                }
                else
                {
                    // TryAdd: newer=destructed ([0x00]) -> destructed wins; newer=new ([0x01]) -> keep older.
                    using NoOpPin firstBytePin = r.PinBuffer(sdOff, 1);
                    if (firstBytePin.Buffer[0] == 0x00)
                    {
                        sdSrcJ = j;
                        sdValOff = sdOff;
                        sdValLen = sdLen;
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
    /// Merge a single storage-trie sub-tag (0x01 compact or 0x02 fallback) across the M
    /// matching per-address sources into <paramref name="perAddrBuilder"/>. Each source's
    /// sub-tag value is an inner HSST(BTree) keyed by encoded TreePath; values are
    /// NodeRefs (NWayMergeSnapshots converts every Full input to Linked first). When
    /// only one source has the sub-tag, copies its bytes verbatim. With multiple sources,
    /// runs an N-way streaming merge into a fixed-size <see cref="HsstPackedArrayBuilder{TWriter}"/>
    /// (innerKeySize → NodeRef.Size). Newest wins on key collision; storage trie nodes
    /// are content-addressable so duplicate keys carry identical NodeRefs in practice.
    /// </summary>
    private static void MergeStorageTrieSubTag<TWriter>(
        int[] matchingSources, int matchCount,
        WholeReadSession[] sessions,
        (long Offset, long Length)[] perAddrBounds,
        ref HsstDenseByteIndexBuilder<TWriter> perAddrBuilder,
        byte[] subTag,
        int innerKeySize) where TWriter : IByteBufferWriter
    {
        using ArrayPoolList<int> srcsList = new(matchCount, matchCount);
        using ArrayPoolList<(long Offset, long Length)> boundsList = new(matchCount, matchCount);
        int[] srcs = srcsList.UnsafeGetInternalArray();
        (long Offset, long Length)[] subBounds = boundsList.UnsafeGetInternalArray();

        int active = 0;
        for (int j = 0; j < matchCount; j++)
        {
            WholeReadSessionReader r = sessions[matchingSources[j]].GetReader();
            if (TryGetBound<WholeReadSessionReader, NoOpPin>(
                    in r, new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length),
                    subTag, out long subOff, out long subLen)
                && subLen > 0)
            {
                srcs[active] = j;
                subBounds[active] = (subOff, subLen);
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

        // Multi-source: streaming N-way merge into a PackedArray.
        using ArrayPoolList<HsstMergeEnumerator> innerEnumsList = new(active, active);
        using ArrayPoolList<bool> innerHasMoreList = new(active, active);
        HsstMergeEnumerator[] innerEnums = innerEnumsList.UnsafeGetInternalArray();
        bool[] innerHasMore = innerHasMoreList.UnsafeGetInternalArray();

        try
        {
            for (int j = 0; j < active; j++)
            {
                WholeReadSessionReader r = sessions[matchingSources[srcs[j]]].GetReader();
                innerEnums[j] = new HsstMergeEnumerator(in r, new Bound(subBounds[j].Offset, subBounds[j].Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
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
                    Bound bJ = innerEnums[j].CurrentKey;
                    Bound bM = innerEnums[minIdx].CurrentKey;
                    WholeReadSessionReader rJ = sessions[matchingSources[srcs[j]]].GetReader();
                    WholeReadSessionReader rM = sessions[matchingSources[srcs[minIdx]]].GetReader();
                    using NoOpPin pinJ = rJ.PinBuffer(bJ.Offset, bJ.Length);
                    using NoOpPin pinM = rM.PinBuffer(bM.Offset, bM.Length);
                    int cmp = pinJ.Buffer.SequenceCompareTo(pinM.Buffer);
                    if (cmp < 0) minIdx = j;
                    else if (cmp == 0) minIdx = j; // newer (higher j) wins
                }
                if (minIdx < 0) break;

                Bound kb = innerEnums[minIdx].CurrentKey;
                Bound vb = innerEnums[minIdx].CurrentValue;
                WholeReadSessionReader rMin = sessions[matchingSources[srcs[minIdx]]].GetReader();
                using NoOpPin keyPin = rMin.PinBuffer(kb.Offset, kb.Length);
                using NoOpPin valPin = rMin.PinBuffer(vb.Offset, vb.Length);
                ReadOnlySpan<byte> minKey = keyPin.Buffer;
                innerBuilder.Add(minKey, valPin.Buffer);

                for (int j = 0; j < active; j++)
                {
                    if (j == minIdx || !innerHasMore[j]) continue;
                    Bound jKey = innerEnums[j].CurrentKey;
                    WholeReadSessionReader rJ = sessions[matchingSources[srcs[j]]].GetReader();
                    using NoOpPin pinJ = rJ.PinBuffer(jKey.Offset, jKey.Length);
                    if (pinJ.Buffer.SequenceCompareTo(minKey) == 0)
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
            for (int j = 0; j < active; j++) innerEnums[j]?.Dispose();
        }
    }

    /// <summary>
    /// N-way metadata merge: from_block/from_hash from oldest, to_block/to_hash/version from newest.
    /// Injects noderefs=[0x01] and ref_ids from referencedIds set.
    /// Emits in sorted key order.
    /// </summary>
    internal static void NWayMetadataMerge<TWriter>(
        PersistedSnapshotList snapshots, ref TWriter writer, HashSet<int> refIds) where TWriter : IByteBufferWriter
    {
        int n = snapshots.Count;
        using WholeReadSession oldestSession = snapshots[0].BeginWholeReadSession();
        using WholeReadSession newestSession = snapshots[n - 1].BeginWholeReadSession();
        WholeReadSessionReader oldestReader = oldestSession.GetReader();
        WholeReadSessionReader newestReader = newestSession.GetReader();

        // Pin the metadata blobs (small, ~100 B); span-based TryGet then walks them
        // for individual fields without further reader plumbing.
        TryGetBound<WholeReadSessionReader, NoOpPin>(in oldestReader, new Bound(0, oldestReader.Length), PersistedSnapshot.MetadataTag, out long oldestMetaOff, out long oldestMetaLen);
        TryGetBound<WholeReadSessionReader, NoOpPin>(in newestReader, new Bound(0, newestReader.Length), PersistedSnapshot.MetadataTag, out long newestMetaOff, out long newestMetaLen);

        using NoOpPin oldestMetaPin = oldestReader.PinBuffer(oldestMetaOff, oldestMetaLen);
        using NoOpPin newestMetaPin = newestReader.PinBuffer(newestMetaOff, newestMetaLen);
        ReadOnlySpan<byte> oldestMeta = oldestMetaPin.Buffer;
        ReadOnlySpan<byte> newestMeta = newestMetaPin.Buffer;

        // Extract fields
        TryGet(oldestMeta, "from_block"u8, out ReadOnlySpan<byte> fromBlock);
        TryGet(oldestMeta, "from_hash"u8, out ReadOnlySpan<byte> fromHash);
        TryGet(newestMeta, "to_block"u8, out ReadOnlySpan<byte> toBlock);
        TryGet(newestMeta, "to_hash"u8, out ReadOnlySpan<byte> toHash);
        TryGet(newestMeta, "version"u8, out ReadOnlySpan<byte> version);

        // Build ref_ids value
        byte[] refIdsValue = new byte[refIds.Count * 4];
        int idx = 0;
        foreach (int id in refIds)
        {
            BitConverter.TryWriteBytes(refIdsValue.AsSpan(idx * 4, 4), id);
            idx++;
        }

        using HsstBuilder<TWriter> builder = new(ref writer);

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

    private static unsafe void AddSlotKeysToBloom(ReadOnlySpan<byte> slotSection, ulong addrKey, BloomFilter bloom)
    {
        // slotSection is a 2-level HSST: prefix(31 bytes) → inner ByteTagMap(suffix(1 byte) → slot value)
        // No session is available here (slot section is sliced from a parent column) so we pin
        // the span ourselves and feed its pointer into a WholeReadSessionReader.
        Span<byte> fullSlot = stackalloc byte[32];
        fixed (byte* slotSectionPtr = slotSection)
        {
        WholeReadSessionReader outerReader = new(slotSectionPtr, slotSection.Length);
        HsstMergeEnumerator outerEnum = new(in outerReader, new Bound(0, slotSection.Length));
        while (outerEnum.MoveNext(in outerReader))
        {
            Bound okb = outerEnum.CurrentKey;
            slotSection.Slice((int)okb.Offset, checked((int)okb.Length)).CopyTo(fullSlot);
            Bound ovb = outerEnum.CurrentValue;
            ReadOnlySpan<byte> innerSection = slotSection.Slice((int)ovb.Offset, checked((int)ovb.Length));
            fixed (byte* innerPtr = innerSection)
            {
            WholeReadSessionReader innerReader = new(innerPtr, innerSection.Length);
            HsstMergeEnumerator innerEnum = new(in innerReader, new Bound(0, innerSection.Length));
            while (innerEnum.MoveNext(in innerReader))
            {
                Bound ikb = innerEnum.CurrentKey;
                innerSection.Slice((int)ikb.Offset, checked((int)ikb.Length)).CopyTo(fullSlot[31..]);
                ulong s0 = MemoryMarshal.Read<ulong>(fullSlot);
                ulong s1 = MemoryMarshal.Read<ulong>(fullSlot[8..]);
                ulong s2 = MemoryMarshal.Read<ulong>(fullSlot[16..]);
                ulong s3 = MemoryMarshal.Read<ulong>(fullSlot[24..]);
                bloom.Add(addrKey ^ s0 ^ s1 ^ s2 ^ s3);
            }
            innerEnum.Dispose();
            } // fixed innerPtr
        }
        outerEnum.Dispose();
        } // fixed slotSectionPtr
    }
}
