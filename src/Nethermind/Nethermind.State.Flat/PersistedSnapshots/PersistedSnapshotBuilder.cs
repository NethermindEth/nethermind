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
/// </summary>
public static class PersistedSnapshotBuilder
{
    private const int TopPathThreshold = 5;
    private const int CompactPathThreshold = 15;
    private const int StorageHashPrefixLength = 20;

    // Outer HSST column tags in iteration order. Shared between ConvertFullToLinked and NWayMergeSnapshots.
    private static readonly byte[][] s_columnTags =
    [
        PersistedSnapshot.MetadataTag,
        PersistedSnapshot.AccountColumnTag,
        PersistedSnapshot.StateNodeTag,
        PersistedSnapshot.StateTopNodesTag,
        PersistedSnapshot.StateNodeFallbackTag,
        PersistedSnapshot.StorageNodeTag,
        PersistedSnapshot.StorageNodeFallbackTag,
    ];

    private static readonly Comparison<(TreePath Path, TrieNode Node)> StateNodeComparer = (a, b) =>
    {
        int cmp = a.Path.Path.Bytes.SequenceCompareTo(b.Path.Path.Bytes);
        return cmp != 0 ? cmp : a.Path.Length.CompareTo(b.Path.Length);
    };

    private static readonly Comparison<((Hash256 Addr, TreePath Path) Key, TrieNode Node)> StorageNodeComparer = (a, b) =>
    {
        int cmp = a.Key.Addr.Bytes.SequenceCompareTo(b.Key.Addr.Bytes);
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
        value = data.Slice((int)b.Offset, b.Length);
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
        offset = (int)b.Offset;
        length = b.Length;
        return true;
    }

    public static void Build<TWriter>(Snapshot snapshot, ref TWriter writer, BloomFilter? bloom = null) where TWriter : IByteBufferWriter
    {
        // Declare mutable locals populated by the parallel jobs below.
        ArrayPoolList<(TreePath Path, TrieNode Node)> stateTop = null!, stateCompact = null!, stateFallback = null!;
        ArrayPoolList<((Hash256 Addr, TreePath Path) Key, TrieNode Node)> storCompact = null!, storFallback = null!;
        ArrayPoolList<((Address Addr, UInt256 Slot) Key, SlotValue? Value)> sortedStorages = null!;
        ArrayPoolList<Address> uniqueAddresses = null!;

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
                // Job C: account column prep — build sorted storages and unique address list.
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
                storages.Sort((a, b) =>
                {
                    int cmp = a.Key.Addr.Bytes.SequenceCompareTo(b.Key.Addr.Bytes);
                    if (cmp != 0) return cmp;
                    return a.Key.Slot.CompareTo(b.Key.Slot);
                });

                ArrayPoolList<Address> addrs = new(Math.Max(1, seen.Count));
                foreach (HashedKey<Address> addr in seen)
                    addrs.Add(addr);
                addrs.Sort((a, b) => a.Bytes.SequenceCompareTo(b.Bytes));

                sortedStorages = storages;
                uniqueAddresses = addrs;
            });

        HsstBuilder<TWriter> outer = new(ref writer);
        try
        {
            // Column 0x00: Metadata
            WriteMetadataColumn(ref outer, snapshot);

            // Column 0x01: Unified account column (accounts, self-destruct, storage)
            WriteAccountColumn(ref outer, snapshot, sortedStorages, uniqueAddresses, bloom);

            // Column 0x03: State nodes (compact, path length 6-15)
            WriteStateNodesColumnCompact(ref outer, stateCompact);

            // Column 0x05: State top nodes (path length 0-5)
            WriteStateTopNodesColumn(ref outer, stateTop);

            // Column 0x06: State nodes fallback (path length 16+)
            WriteStateNodesColumnFallback(ref outer, stateFallback);

            // Column 0x07: Storage nodes (compact, path length 6-15)
            WriteStorageNodesColumnCompact(ref outer, storCompact);

            // Column 0x08: Storage nodes fallback (path length 16+)
            WriteStorageNodesColumnFallback(ref outer, storFallback);

            outer.Build();
        }
        finally
        {
            outer.Dispose();
            sortedStorages?.Dispose();
            uniqueAddresses?.Dispose();
            stateTop?.Dispose();
            stateCompact?.Dispose();
            stateFallback?.Dispose();
            storCompact?.Dispose();
            storFallback?.Dispose();
        }
    }

    public static int EstimateSize(Snapshot snapshot) =>
        // Use a conservative multiplier on the snapshot memory estimate.
        // Clamp to 1 GiB so the buffer stays within ArrayPool's poolable range,
        // and all arithmetic is done in long to avoid int overflow for large snapshots.
        (int)Math.Min(1.GiB, snapshot.EstimateMemory() + 1.KiB);

    private static void WriteMetadataColumn<TWriter>(ref HsstBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Metadata keys must be in sorted order (ASCII): "from_block" < "from_hash" < "to_block" < "to_hash" < "version"
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> inner = new(ref innerWriter);

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
        ref HsstBuilder<TWriter> outer, Snapshot snapshot,
        ArrayPoolList<((Address Addr, UInt256 Slot) Key, SlotValue? Value)> sortedStorages,
        ArrayPoolList<Address> uniqueAddresses,
        BloomFilter? bloom = null) where TWriter : IByteBufferWriter
    {
        const int slotPrefixLength = 30;
        const int slotSuffixLength = 2;

        // Address-level HSST
        ref TWriter addressWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> addressLevel = new(ref addressWriter, minSeparatorLength: 2);
        byte[] rlpBuffer = new byte[256];
        RlpStream rlpStream = new(rlpBuffer);
        Span<byte> slotKey = stackalloc byte[32];
        Span<byte> currentPrefixBuf = stackalloc byte[slotPrefixLength];
        int storageIdx = 0;

        foreach (Address address in uniqueAddresses)
        {
            ulong addrBloomKey = 0;
            if (bloom is not null)
            {
                addrBloomKey = PersistedSnapshotBloomBuilder.AddressKey(address);
                bloom.Add(addrBloomKey);
            }

            // Begin per-address HSST
            ref TWriter perAddrWriter = ref addressLevel.BeginValueWrite();
            using HsstBuilder<TWriter> perAddr = new(ref perAddrWriter);

            // Sub-tag 0x01: Slots
            bool hasStorage = storageIdx < sortedStorages.Count &&
                sortedStorages[storageIdx].Key.Addr.Bytes.SequenceEqual(address.Bytes);
            if (hasStorage)
            {
                ref TWriter slotWriter = ref perAddr.BeginValueWrite();
                using HsstBuilder<TWriter> prefixLevel = new(ref slotWriter, minSeparatorLength: 2);

                while (storageIdx < sortedStorages.Count &&
                    sortedStorages[storageIdx].Key.Addr.Bytes.SequenceEqual(address.Bytes))
                {
                    sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                    slotKey[..slotPrefixLength].CopyTo(currentPrefixBuf);
                    ReadOnlySpan<byte> currentPrefix = currentPrefixBuf;

                    ref TWriter suffixWriter = ref prefixLevel.BeginValueWrite();
                    using HsstBuilder<TWriter> suffixLevel = new(ref suffixWriter, minSeparatorLength: 2, inlineValues: true);

                    while (storageIdx < sortedStorages.Count &&
                        sortedStorages[storageIdx].Key.Addr.Bytes.SequenceEqual(address.Bytes))
                    {
                        sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey);
                        if (!slotKey[..slotPrefixLength].SequenceEqual(currentPrefix))
                            break;

                        SlotValue? value = sortedStorages[storageIdx].Value;
                        if (value.HasValue)
                        {
                            ReadOnlySpan<byte> withoutLeadingZeros = value.Value.AsReadOnlySpan.WithoutLeadingZeros();
                            suffixLevel.Add(slotKey.Slice(slotPrefixLength, slotSuffixLength), withoutLeadingZeros);
                        }
                        else
                        {
                            suffixLevel.Add(slotKey.Slice(slotPrefixLength, slotSuffixLength), []);
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

            // Sub-tag 0x02: Self-destruct
            if (snapshot.Content.SelfDestructedStorageAddresses.TryGetValue(address, out bool sdValue))
            {
                perAddr.Add(PersistedSnapshot.SelfDestructSubTag, sdValue ? [0x01] : []);
            }

            // Sub-tag 0x03: Account
            if (snapshot.TryGetAccount(address, out Account? account))
            {
                if (account is null)
                {
                    perAddr.Add(PersistedSnapshot.AccountSubTag, []);
                }
                else
                {
                    int len = AccountDecoder.Slim.GetLength(account);
                    rlpStream.Reset();
                    AccountDecoder.Slim.Encode(rlpStream, account);
                    perAddr.Add(PersistedSnapshot.AccountSubTag, rlpBuffer.AsSpan(0, len));
                }
            }

            perAddr.Build();
            addressLevel.FinishValueWrite(address.Bytes);
        }

        addressLevel.Build();
        outer.FinishValueWrite(PersistedSnapshot.AccountColumnTag);
    }

    private static void WriteStateTopNodesColumn<TWriter>(ref HsstBuilder<TWriter> outer, ArrayPoolList<(TreePath Path, TrieNode Node)> stateNodes) where TWriter : IByteBufferWriter
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> inner = new(ref innerWriter, minSeparatorLength: 3);
        Span<byte> keyBuffer = stackalloc byte[3];
        foreach ((TreePath path, TrieNode node) in stateNodes)
        {
            path.EncodeWith3Byte(keyBuffer);
            inner.Add(keyBuffer, node.FullRlp.AsSpan());
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshot.StateTopNodesTag);
    }

    private static void WriteStateNodesColumnCompact<TWriter>(ref HsstBuilder<TWriter> outer, ArrayPoolList<(TreePath Path, TrieNode Node)> stateNodes) where TWriter : IByteBufferWriter
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> inner = new(ref innerWriter, minSeparatorLength: 8);
        Span<byte> keyBuffer = stackalloc byte[8];
        foreach ((TreePath path, TrieNode node) in stateNodes)
        {
            path.EncodeWith8Byte(keyBuffer);
            inner.Add(keyBuffer, node.FullRlp.AsSpan());
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshot.StateNodeTag);
    }

    private static void WriteStateNodesColumnFallback<TWriter>(ref HsstBuilder<TWriter> outer, ArrayPoolList<(TreePath Path, TrieNode Node)> stateNodes) where TWriter : IByteBufferWriter
    {
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> inner = new(ref innerWriter);
        Span<byte> keyBuffer = stackalloc byte[33];
        foreach ((TreePath path, TrieNode node) in stateNodes)
        {
            path.Path.Bytes.CopyTo(keyBuffer);
            keyBuffer[32] = (byte)path.Length;
            inner.Add(keyBuffer, node.FullRlp.AsSpan());
        }

        inner.Build();
        outer.FinishValueWrite(PersistedSnapshot.StateNodeFallbackTag);
    }

    private static void WriteStorageNodesColumnCompact<TWriter>(ref HsstBuilder<TWriter> outer, ArrayPoolList<((Hash256 Addr, TreePath Path) Key, TrieNode Node)> storageNodes) where TWriter : IByteBufferWriter
    {
        // Hash-level HSST: Hash256(32) -> inner HSST(TreePath(8) -> NodeRLP)
        ref TWriter hashWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> hashLevel = new(ref hashWriter, minSeparatorLength: 2);
        Span<byte> pathKey = stackalloc byte[8];
        int i = 0;
        while (i < storageNodes.Count)
        {
            Hash256 currentHash = storageNodes[i].Key.Addr;

            ref TWriter innerWriter = ref hashLevel.BeginValueWrite();
            using HsstBuilder<TWriter> inner = new(ref innerWriter, minSeparatorLength: 8);

            while (i < storageNodes.Count && storageNodes[i].Key.Addr.Equals(currentHash))
            {
                ((Hash256 _, TreePath path) snKey, TrieNode node) = storageNodes[i];
                snKey.path.EncodeWith8Byte(pathKey);
                inner.Add(pathKey, node.FullRlp.AsSpan());
                i++;
            }

            inner.Build();
            hashLevel.FinishValueWrite(currentHash.Bytes[..StorageHashPrefixLength]);
        }

        hashLevel.Build();
        outer.FinishValueWrite(PersistedSnapshot.StorageNodeTag);
    }

    private static void WriteStorageNodesColumnFallback<TWriter>(ref HsstBuilder<TWriter> outer, ArrayPoolList<((Hash256 Addr, TreePath Path) Key, TrieNode Node)> storageNodes) where TWriter : IByteBufferWriter
    {
        // Hash-level HSST: Hash256(32) -> inner HSST(TreePath(33) -> NodeRLP)
        ref TWriter hashWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> hashLevel = new(ref hashWriter, minSeparatorLength: 2);
        Span<byte> pathKey = stackalloc byte[33];
        int i = 0;
        while (i < storageNodes.Count)
        {
            Hash256 currentHash = storageNodes[i].Key.Addr;

            ref TWriter innerWriter = ref hashLevel.BeginValueWrite();
            using HsstBuilder<TWriter> inner = new(ref innerWriter);

            while (i < storageNodes.Count && storageNodes[i].Key.Addr.Equals(currentHash))
            {
                ((Hash256 _, TreePath path) snKey, TrieNode node) = storageNodes[i];
                snKey.path.Path.Bytes.CopyTo(pathKey);
                pathKey[32] = (byte)snKey.path.Length;
                inner.Add(pathKey, node.FullRlp.AsSpan());
                i++;
            }

            inner.Build();
            hashLevel.FinishValueWrite(currentHash.Bytes[..StorageHashPrefixLength]);
        }

        hashLevel.Build();
        outer.FinishValueWrite(PersistedSnapshot.StorageNodeFallbackTag);
    }

    /// <summary>
    /// Convert a Full snapshot into a Linked snapshot where trie RLP columns have NodeRefs.
    /// Account column (0x01) is copied as-is. Metadata column (0x00) is copied as-is.
    /// Trie columns (0x03, 0x05, 0x06) have values replaced with NodeRef(snapshotId, offset).
    /// Nested trie columns (0x07, 0x08) have inner values replaced with NodeRefs.
    /// </summary>
    internal static void ConvertFullToLinked<TWriter>(PersistedSnapshot fullSnapshot, ref TWriter writer) where TWriter : IByteBufferWriter
    {
        using WholeReadSession session = fullSnapshot.BeginWholeReadSession();
        ReadOnlySpan<byte> snapshotData = session.GetSpan();
        using HsstBuilder<TWriter> outerBuilder = new(ref writer);

        int snapshotId = fullSnapshot.Id;

        foreach (byte[] tag in s_columnTags)
        {
            if (!TryGet(snapshotData, tag, out ReadOnlySpan<byte> column)) continue;
            int columnOffset = SpanOffset(snapshotData, column);

            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();

            switch (tag[0])
            {
                // Metadata and account: copy as-is
                case 0x00 or 0x01:
                    CopyColumn(column, ref valueWriter);
                    break;
                // Flat trie columns: convert values to NodeRefs
                case 0x03:
                    ConvertFlatColumnToNodeRefs(column, ref valueWriter, snapshotId, columnOffset, minSeparatorLength: 8);
                    break;
                case 0x05:
                    ConvertFlatColumnToNodeRefs(column, ref valueWriter, snapshotId, columnOffset, minSeparatorLength: 3);
                    break;
                case 0x06:
                    ConvertFlatColumnToNodeRefs(column, ref valueWriter, snapshotId, columnOffset);
                    break;
                // Nested trie columns: convert inner values to NodeRefs
                case 0x07:
                    ConvertNestedColumnToNodeRefs(column, snapshotData, ref valueWriter, snapshotId, outerMinSep: 2, innerMinSep: 8);
                    break;
                case 0x08:
                    ConvertNestedColumnToNodeRefs(column, snapshotData, ref valueWriter, snapshotId, outerMinSep: 2);
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
        int minSeparatorLength = 0) where TWriter : IByteBufferWriter
    {
        SpanByteReader reader = new(column);
        HsstBuilder<TWriter> builder = new(ref writer, minSeparatorLength, inlineValues: true);
        using HsstEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, column.Length));
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];

        while (e.MoveNext())
        {
            KeyValueEntry cur = e.Current;
            // metaStart relative to column = ValueBound.Offset + ValueBound.Length
            int metaStart = (int)(cur.ValueBound.Offset + cur.ValueBound.Length);
            NodeRef.Write(refBytes, new NodeRef(snapshotId, columnOffset + metaStart));
            builder.Add(column.Slice((int)cur.KeyBound.Offset, cur.KeyBound.Length), refBytes);
        }

        builder.Build();
        builder.Dispose();
    }

    /// <summary>
    /// Convert a nested trie column (storage nodes) to NodeRefs.
    /// Outer keys (address hash prefixes) are preserved. Inner values are replaced with NodeRefs.
    /// </summary>
    private static void ConvertNestedColumnToNodeRefs<TWriter>(
        ReadOnlySpan<byte> column, ReadOnlySpan<byte> snapshotData, ref TWriter writer,
        int snapshotId,
        int outerMinSep = 0, int innerMinSep = 0) where TWriter : IByteBufferWriter
    {
        int columnOffsetInSnapshot = SpanOffset(snapshotData, column);
        SpanByteReader reader = new(column);
        HsstBuilder<TWriter> builder = new(ref writer, outerMinSep);
        using HsstEnumerator<SpanByteReader, NoOpPin> outerEnum = new(in reader, new Bound(0, column.Length));
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];

        while (outerEnum.MoveNext())
        {
            Bound innerScope = outerEnum.Current.ValueBound;

            ref TWriter innerWriter = ref builder.BeginValueWrite();
            HsstBuilder<TWriter> innerBuilder = new(ref innerWriter, innerMinSep, inlineValues: true);
            using HsstEnumerator<SpanByteReader, NoOpPin> innerEnum = new(in reader, innerScope);

            while (innerEnum.MoveNext())
            {
                KeyValueEntry inner = innerEnum.Current;
                // metaStart relative to column for the inner entry; add columnOffsetInSnapshot
                // to land at the absolute snapshot offset NodeRef expects.
                int metaStartInColumn = (int)(inner.ValueBound.Offset + inner.ValueBound.Length);
                NodeRef.Write(refBytes, new NodeRef(snapshotId, columnOffsetInSnapshot + metaStartInColumn));
                innerBuilder.Add(column.Slice((int)inner.KeyBound.Offset, inner.KeyBound.Length), refBytes);
            }

            innerBuilder.Build();
            innerBuilder.Dispose();
            builder.FinishValueWrite(column.Slice((int)outerEnum.Current.KeyBound.Offset, outerEnum.Current.KeyBound.Length));
        }

        builder.Build();
        builder.Dispose();
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
                    int estimatedSize = snapshots[i].Size / 2 + 4096;
                    using ArenaWriter tempWriter = tempArena.CreateWriter(Math.Max(estimatedSize, snapshots[i].Size));
                    ConvertFullToLinked(snapshots[i], ref tempWriter.GetWriter());
                    (_, ArenaReservation tempRes) = tempWriter.Complete();
                    PersistedSnapshot convertedSnap = new(snapshots[i].Id, snapshots[i].From, snapshots[i].To,
                        PersistedSnapshotType.Linked, tempRes);
                    mergeSnapshots.Add(convertedSnap);
                }
                else
                {
                    if (!snapshots[i].TryAcquire())
                        throw new InvalidOperationException("Cannot acquire lease for snapshot");
                    mergeSnapshots.Add(snapshots[i]);
                }
            }

            using HsstBuilder<TWriter> outerBuilder = new(ref writer);

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
                        NWayStreamingMerge(mergeSnapshots, tag, ref valueWriter,
                            minSeparatorLength: 8, inlineValues: true);
                        break;
                    case 0x05:
                        NWayStreamingMerge(mergeSnapshots, tag, ref valueWriter,
                            minSeparatorLength: 3, inlineValues: true);
                        break;
                    case 0x06:
                        NWayStreamingMerge(mergeSnapshots, tag, ref valueWriter,
                            inlineValues: true);
                        break;
                    case 0x07:
                        NWayNestedStreamingMerge(mergeSnapshots, tag, ref valueWriter,
                            outerMinSep: 2, innerMinSep: 8, innerInline: true);
                        break;
                    case 0x08:
                        NWayNestedStreamingMerge(mergeSnapshots, tag, ref valueWriter,
                            outerMinSep: 2, innerInline: true);
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
        int minSeparatorLength = 0, bool inlineValues = false) where TWriter : IByteBufferWriter
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstMergeEnumerator> enums = new(n, n);
        using ArrayPoolList<bool> hasMore = new(n, n);
        using ArrayPoolList<(int Offset, int Length)> columnBounds = new(n, n);
        using ArrayPoolList<WholeReadSession> sessions = new(n, n);

        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                ReadOnlySpan<byte> snapshotData = sessions[i].GetSpan();
                columnBounds[i] = TryGetBound(snapshotData, tag, out int colOff, out int colLen) ? (colOff, colLen) : (0, 0);
                ReadOnlySpan<byte> column = snapshotData.Slice(columnBounds[i].Offset, columnBounds[i].Length);
                enums[i] = new HsstMergeEnumerator(column, isInline: inlineValues);
                hasMore[i] = enums[i].MoveNext(column);
            }

            using HsstBuilder<TWriter> builder = new(ref writer, minSeparatorLength, inlineValues);

            while (true)
            {
                // Find min key across all active enumerators, newest wins on tie
                int minIdx = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!hasMore[i]) continue;
                    if (minIdx < 0)
                    {
                        minIdx = i;
                        continue;
                    }
                    int cmp = enums[i].CurrentKey.SequenceCompareTo(enums[minIdx].CurrentKey);
                    if (cmp < 0) minIdx = i;
                    else if (cmp == 0) minIdx = i; // newer (higher index) wins
                }

                if (minIdx < 0) break;

                ReadOnlySpan<byte> minKey = enums[minIdx].CurrentKey;
                ReadOnlySpan<byte> colSpan = sessions[minIdx].GetSpan().Slice(columnBounds[minIdx].Offset, columnBounds[minIdx].Length);
                (int valOff, int valLen) = enums[minIdx].GetCurrentValueBound(colSpan);
                builder.Add(minKey, colSpan.Slice(valOff, valLen));

                // Advance all enumerators that had the min key.
                // Advance minIdx LAST because minKey references its _keyBuffer which MoveNext overwrites.
                for (int i = 0; i < n; i++)
                {
                    if (i == minIdx || !hasMore[i]) continue;
                    if (enums[i].CurrentKey.SequenceCompareTo(minKey) == 0)
                    {
                        ReadOnlySpan<byte> cs = sessions[i].GetSpan().Slice(columnBounds[i].Offset, columnBounds[i].Length);
                        hasMore[i] = enums[i].MoveNext(cs);
                    }
                }
                {
                    ReadOnlySpan<byte> cs = sessions[minIdx].GetSpan().Slice(columnBounds[minIdx].Offset, columnBounds[minIdx].Length);
                    hasMore[minIdx] = enums[minIdx].MoveNext(cs);
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
        Func<int, ReadOnlySpan<byte>> getColumnSpan,
        ref TWriter writer,
        int outerMinSep = 0, int innerMinSep = 0, bool innerInline = false) where TWriter : IByteBufferWriter
    {
        using HsstBuilder<TWriter> builder = new(ref writer, outerMinSep);

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
                int cmp = enums[i].CurrentKey.SequenceCompareTo(enums[minIdx].CurrentKey);
                if (cmp < 0) minIdx = i;
            }

            if (minIdx < 0) break;

            ReadOnlySpan<byte> minKey = enums[minIdx].CurrentKey;

            // Collect all sources with this key
            int matchCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (hasMore[i] && enums[i].CurrentKey.SequenceCompareTo(minKey) == 0)
                    matchingSources[matchCount++] = i;
            }

            if (matchCount == 1)
            {
                // Single source: copy as-is
                int srcIdx = matchingSources[0];
                ReadOnlySpan<byte> cs = getColumnSpan(srcIdx);
                (int valOff, int valLen) = enums[srcIdx].GetCurrentValueBound(cs);
                builder.Add(minKey, cs.Slice(valOff, valLen));
            }
            else
            {
                // M sources: create M inner enumerators and merge
                ref TWriter innerWriter = ref builder.BeginValueWrite();
                NWayInnerMerge(enums, matchingSources, matchCount, getColumnSpan,
                    ref innerWriter, innerMinSep, innerInline);
                builder.FinishValueWrite(minKey);
            }

            // Advance all matching
            for (int j = 0; j < matchCount; j++)
            {
                int i = matchingSources[j];
                hasMore[i] = enums[i].MoveNext(getColumnSpan(i));
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
        Func<int, ReadOnlySpan<byte>> getColumnSpan,
        ref TWriter writer,
        int minSeparatorLength = 0, bool inlineValues = false) where TWriter : IByteBufferWriter
    {
        using ArrayPoolList<HsstMergeEnumerator> innerEnums = new(matchCount, matchCount);
        using ArrayPoolList<bool> innerHasMore = new(matchCount, matchCount);
        using ArrayPoolList<(int Offset, int Length)> innerBounds = new(matchCount, matchCount);

        try
        {
            for (int j = 0; j < matchCount; j++)
            {
                int srcIdx = matchingSources[j];
                ReadOnlySpan<byte> cs = getColumnSpan(srcIdx);
                innerBounds[j] = outerEnums[srcIdx].GetCurrentValueBound(cs);
                ReadOnlySpan<byte> innerSpan = cs.Slice(innerBounds[j].Offset, innerBounds[j].Length);
                innerEnums[j] = new HsstMergeEnumerator(innerSpan, isInline: inlineValues);
                innerHasMore[j] = innerEnums[j].MoveNext(innerSpan);
            }

            using HsstBuilder<TWriter> builder = new(ref writer, minSeparatorLength, inlineValues);

            while (true)
            {
                int minIdx = -1;
                for (int j = 0; j < matchCount; j++)
                {
                    if (!innerHasMore[j]) continue;
                    if (minIdx < 0)
                    {
                        minIdx = j;
                        continue;
                    }
                    int cmp = innerEnums[j].CurrentKey.SequenceCompareTo(innerEnums[minIdx].CurrentKey);
                    if (cmp < 0) minIdx = j;
                    else if (cmp == 0) minIdx = j; // newer (higher j = higher source index) wins
                }

                if (minIdx < 0) break;

                ReadOnlySpan<byte> minKey = innerEnums[minIdx].CurrentKey;
                ReadOnlySpan<byte> innerSpan = getColumnSpan(matchingSources[minIdx]).Slice(innerBounds[minIdx].Offset, innerBounds[minIdx].Length);
                (int valOff, int valLen) = innerEnums[minIdx].GetCurrentValueBound(innerSpan);
                builder.Add(minKey, innerSpan.Slice(valOff, valLen));

                // Advance all with min key.
                // Advance minIdx LAST because minKey references its _keyBuffer which MoveNext overwrites.
                for (int j = 0; j < matchCount; j++)
                {
                    if (j == minIdx || !innerHasMore[j]) continue;
                    if (innerEnums[j].CurrentKey.SequenceCompareTo(minKey) == 0)
                        innerHasMore[j] = innerEnums[j].MoveNext(getColumnSpan(matchingSources[j]).Slice(innerBounds[j].Offset, innerBounds[j].Length));
                }
                innerHasMore[minIdx] = innerEnums[minIdx].MoveNext(getColumnSpan(matchingSources[minIdx]).Slice(innerBounds[minIdx].Offset, innerBounds[minIdx].Length));
            }

            builder.Build();
        }
        finally
        {
            for (int j = 0; j < matchCount; j++) innerEnums[j]?.Dispose();
        }
    }

    /// <summary>
    /// N-way nested streaming merge across N persisted snapshots.
    /// Initializes enumerators from snapshot data and delegates to the core merge method.
    /// </summary>
    internal static void NWayNestedStreamingMerge<TWriter>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer,
        int outerMinSep = 0, int innerMinSep = 0, bool innerInline = false) where TWriter : IByteBufferWriter
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstMergeEnumerator> enumsList = new(n, n);
        using ArrayPoolList<bool> hasMoreList = new(n, n);
        using ArrayPoolList<(int Offset, int Length)> columnBoundsList = new(n, n);
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        HsstMergeEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        bool[] hasMore = hasMoreList.UnsafeGetInternalArray();
        (int Offset, int Length)[] columnBounds = columnBoundsList.UnsafeGetInternalArray();
        WholeReadSession[] sessions = sessionsList.UnsafeGetInternalArray();

        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                ReadOnlySpan<byte> snapshotData = sessions[i].GetSpan();
                columnBounds[i] = TryGetBound(snapshotData, tag, out int colOff, out int colLen) ? (colOff, colLen) : (0, 0);
                ReadOnlySpan<byte> column = snapshotData.Slice(columnBounds[i].Offset, columnBounds[i].Length);
                enums[i] = new HsstMergeEnumerator(column, isInline: false);
                hasMore[i] = enums[i].MoveNext(column);
            }

            NWayNestedStreamingMerge(enums, hasMore, n,
                i => sessions[i].GetSpan().Slice(columnBounds[i].Offset, columnBounds[i].Length),
                ref writer, outerMinSep, innerMinSep, innerInline);
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i]?.Dispose();
            for (int i = 0; i < n; i++) sessions[i]?.Dispose();
        }
    }

    /// <summary>
    /// N-way merge of the account column (tag 0x01) across N snapshots.
    /// Outer: 20-byte address keys (minSep=2). For matching addresses with M sources,
    /// calls <see cref="NWayMergePerAddressHsst"/>. Single source: copy as-is.
    /// </summary>
    internal static void NWayMergeAccountColumn<TWriter>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer, BloomFilter? bloom = null) where TWriter : IByteBufferWriter
    {
        int n = snapshots.Count;
        using ArrayPoolList<HsstMergeEnumerator> enumsList = new(n, n);
        using ArrayPoolList<bool> hasMoreList = new(n, n);
        using ArrayPoolList<(int Offset, int Length)> columnBoundsList = new(n, n);
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        using ArrayPoolList<int> matchingSourcesList = new(n, n);
        HsstMergeEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        bool[] hasMore = hasMoreList.UnsafeGetInternalArray();
        (int Offset, int Length)[] columnBounds = columnBoundsList.UnsafeGetInternalArray();
        WholeReadSession[] sessions = sessionsList.UnsafeGetInternalArray();
        int[] matchingSources = matchingSourcesList.UnsafeGetInternalArray();

        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                ReadOnlySpan<byte> snapshotData = sessions[i].GetSpan();
                columnBounds[i] = TryGetBound(snapshotData, tag, out int colOff, out int colLen) ? (colOff, colLen) : (0, 0);
                ReadOnlySpan<byte> column = snapshotData.Slice(columnBounds[i].Offset, columnBounds[i].Length);
                enums[i] = new HsstMergeEnumerator(column, isInline: false);
                hasMore[i] = enums[i].MoveNext(column);
            }

            using HsstBuilder<TWriter> builder = new(ref writer, minSeparatorLength: 2);

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
                    int cmp = enums[i].CurrentKey.SequenceCompareTo(enums[minIdx].CurrentKey);
                    if (cmp < 0) minIdx = i;
                }

                if (minIdx < 0) break;

                ReadOnlySpan<byte> minKey = enums[minIdx].CurrentKey;

                int matchCount = 0;
                for (int i = 0; i < n; i++)
                {
                    if (hasMore[i] && enums[i].CurrentKey.SequenceCompareTo(minKey) == 0)
                        matchingSources[matchCount++] = i;
                }

                if (matchCount == 1)
                {
                    int srcIdx = matchingSources[0];
                    ReadOnlySpan<byte> colSpan = sessions[srcIdx].GetSpan().Slice(columnBounds[srcIdx].Offset, columnBounds[srcIdx].Length);
                    (int valOff, int valLen) = enums[srcIdx].GetCurrentValueBound(colSpan);
                    builder.Add(minKey, colSpan.Slice(valOff, valLen));
                    if (bloom is not null)
                    {
                        ulong addrKey = MemoryMarshal.Read<ulong>(minKey);
                        bloom.Add(addrKey);
                        ReadOnlySpan<byte> perAddrHsst = colSpan.Slice(valOff, valLen);
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
                        enums, matchingSources, matchCount, sessions, columnBounds,
                        ref perAddrWriter, bloom, addrKey);
                    builder.FinishValueWrite(minKey);
                }

                for (int j = 0; j < matchCount; j++)
                {
                    int i = matchingSources[j];
                    ReadOnlySpan<byte> cs = sessions[i].GetSpan().Slice(columnBounds[i].Offset, columnBounds[i].Length);
                    hasMore[i] = enums[i].MoveNext(cs);
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
    /// - Slots: find newest destruct barrier, merge slots from barrier..M-1 via nested streaming merge
    /// - SelfDestruct: iterate 0..M-1, apply TryAdd semantics
    /// - Account: newest wins (walk M-1..0, first with AccountSubTag)
    /// </summary>
    private static void NWayMergePerAddressHsst<TWriter>(
        HsstMergeEnumerator[] outerEnums, int[] matchingSources, int matchCount,
        WholeReadSession[] sessions, (int Offset, int Length)[] columnBounds,
        ref TWriter writer, BloomFilter? bloom = null, ulong addrBloomKey = 0) where TWriter : IByteBufferWriter
    {
        // Get per-address HSST bounds (absolute offset from snapshot start) for each matching source
        using ArrayPoolList<(int Offset, int Length)> perAddrBoundsList = new(matchCount, matchCount);
        (int Offset, int Length)[] perAddrBounds = perAddrBoundsList.UnsafeGetInternalArray();
        for (int j = 0; j < matchCount; j++)
        {
            int srcIdx = matchingSources[j];
            ReadOnlySpan<byte> colSpan = sessions[srcIdx].GetSpan().Slice(columnBounds[srcIdx].Offset, columnBounds[srcIdx].Length);
            (int valOff, int valLen) = outerEnums[srcIdx].GetCurrentValueBound(colSpan);
            perAddrBounds[j] = (columnBounds[srcIdx].Offset + valOff, valLen);
        }

        using HsstBuilder<TWriter> perAddrBuilder = new(ref writer);

        // Find newest destruct barrier: newest j where SelfDestructSubTag value is empty (destructed)
        int destructBarrier = -1;
        for (int j = 0; j < matchCount; j++)
        {
            ReadOnlySpan<byte> perAddr = sessions[matchingSources[j]].GetSpan().Slice(perAddrBounds[j].Offset, perAddrBounds[j].Length);
            if (TryGet(perAddr, PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> sdVal) && sdVal.IsEmpty)
                destructBarrier = j;
        }

        // Sub-tag 0x01: Slots
        // Merge slots only from max(0, destructBarrier)..matchCount-1
        int slotStart = Math.Max(0, destructBarrier);

        if (bloom is not null)
        {
            for (int j = slotStart; j < matchCount; j++)
            {
                ReadOnlySpan<byte> perAddr = sessions[matchingSources[j]].GetSpan()
                    .Slice(perAddrBounds[j].Offset, perAddrBounds[j].Length);
                if (TryGet(perAddr, PersistedSnapshot.SlotSubTag, out ReadOnlySpan<byte> slotSection))
                    AddSlotKeysToBloom(slotSection, addrBloomKey, bloom);
            }
        }
        {
            // Collect sources that have slots in the range
            int slotSourceCount = 0;
            int slotCapacity = matchCount - slotStart;
            using ArrayPoolList<int> slotSourcesList = new(slotCapacity, slotCapacity);
            using ArrayPoolList<(int Offset, int Length)> slotBoundsList = new(slotCapacity, slotCapacity);
            int[] slotSources = slotSourcesList.UnsafeGetInternalArray();
            (int Offset, int Length)[] slotBounds = slotBoundsList.UnsafeGetInternalArray();
            for (int j = slotStart; j < matchCount; j++)
            {
                ReadOnlySpan<byte> perAddr = sessions[matchingSources[j]].GetSpan().Slice(perAddrBounds[j].Offset, perAddrBounds[j].Length);
                if (TryGetBound(perAddr, PersistedSnapshot.SlotSubTag, out int slotOff, out int slotLen))
                {
                    slotSources[slotSourceCount] = j;
                    slotBounds[slotSourceCount] = (perAddrBounds[j].Offset + slotOff, slotLen);
                    slotSourceCount++;
                }
            }

            if (slotSourceCount == 1)
            {
                perAddrBuilder.Add(PersistedSnapshot.SlotSubTag, sessions[matchingSources[slotSources[0]]].GetSpan().Slice(slotBounds[0].Offset, slotBounds[0].Length));
            }
            else if (slotSourceCount > 1)
            {
                // N-way nested streaming merge on slot prefix-level HSSTs
                using ArrayPoolList<HsstMergeEnumerator> slotEnumsList = new(slotSourceCount, slotSourceCount);
                using ArrayPoolList<bool> slotHasMoreList = new(slotSourceCount, slotSourceCount);
                HsstMergeEnumerator[] slotEnums = slotEnumsList.UnsafeGetInternalArray();
                bool[] slotHasMore = slotHasMoreList.UnsafeGetInternalArray();
                try
                {
                    for (int j = 0; j < slotSourceCount; j++)
                    {
                        ReadOnlySpan<byte> slotSpan = sessions[matchingSources[slotSources[j]]].GetSpan().Slice(slotBounds[j].Offset, slotBounds[j].Length);
                        slotEnums[j] = new HsstMergeEnumerator(slotSpan, isInline: false);
                        slotHasMore[j] = slotEnums[j].MoveNext(slotSpan);
                    }

                    ref TWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                    NWayNestedStreamingMerge(
                        slotEnums, slotHasMore, slotSourceCount,
                        j => sessions[matchingSources[slotSources[j]]].GetSpan().Slice(slotBounds[j].Offset, slotBounds[j].Length),
                        ref slotWriter,
                        outerMinSep: 2, innerMinSep: 2, innerInline: true);
                    perAddrBuilder.FinishValueWrite(PersistedSnapshot.SlotSubTag);
                }
                finally
                {
                    for (int j = 0; j < slotSourceCount; j++) slotEnums[j]?.Dispose();
                }
            }
        }

        // Sub-tag 0x02: SelfDestruct — iterate 0..M-1, apply TryAdd semantics
        {
            bool hasSd = false;
            ReadOnlySpan<byte> sdResult = default;

            for (int j = 0; j < matchCount; j++)
            {
                ReadOnlySpan<byte> perAddr = sessions[matchingSources[j]].GetSpan().Slice(perAddrBounds[j].Offset, perAddrBounds[j].Length);
                if (!TryGet(perAddr, PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> sdVal)) continue;

                if (!hasSd)
                {
                    // First SD entry
                    hasSd = true;
                    sdResult = sdVal;
                }
                else
                {
                    // TryAdd: newer=empty -> empty, newer=0x01 -> keep older
                    if (sdVal.IsEmpty)
                        sdResult = [];
                    // else newer=0x01 (new account): keep existing sdResult (TryAdd)
                }
            }

            if (hasSd)
                perAddrBuilder.Add(PersistedSnapshot.SelfDestructSubTag, sdResult);
        }

        // Sub-tag 0x03: Account — newest wins (walk M-1..0, first with AccountSubTag)
        {
            for (int j = matchCount - 1; j >= 0; j--)
            {
                ReadOnlySpan<byte> perAddr = sessions[matchingSources[j]].GetSpan().Slice(perAddrBounds[j].Offset, perAddrBounds[j].Length);
                if (TryGet(perAddr, PersistedSnapshot.AccountSubTag, out ReadOnlySpan<byte> account))
                {
                    perAddrBuilder.Add(PersistedSnapshot.AccountSubTag, account);
                    break;
                }
            }
        }

        perAddrBuilder.Build();
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
        ReadOnlySpan<byte> oldestData = oldestSession.GetSpan();
        ReadOnlySpan<byte> newestData = newestSession.GetSpan();

        TryGet(oldestData, PersistedSnapshot.MetadataTag, out ReadOnlySpan<byte> oldestMeta);
        TryGet(newestData, PersistedSnapshot.MetadataTag, out ReadOnlySpan<byte> newestMeta);

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

    private static void AddSlotKeysToBloom(ReadOnlySpan<byte> slotSection, ulong addrKey, BloomFilter bloom)
    {
        // slotSection is a 2-level HSST: prefix(30 bytes) → inner HSST(suffix(2 bytes) → slot value)
        Span<byte> fullSlot = stackalloc byte[32];
        HsstMergeEnumerator outerEnum = new(slotSection, isInline: false);
        while (outerEnum.MoveNext(slotSection))
        {
            outerEnum.CurrentKey.CopyTo(fullSlot);
            ReadOnlySpan<byte> innerSection = outerEnum.GetCurrentValue(slotSection);
            HsstMergeEnumerator innerEnum = new(innerSection, isInline: true);
            while (innerEnum.MoveNext(innerSection))
            {
                innerEnum.CurrentKey.CopyTo(fullSlot[30..]);
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
