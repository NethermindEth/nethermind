// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using Org.BouncyCastle.Operators;

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

    public static void Build<TWriter>(Snapshot snapshot, ref TWriter writer) where TWriter : IByteBufferWriter
    {
        HsstBuilder<TWriter> outer = new(ref writer);
        try
        {
            // Column 0x00: Metadata
            WriteMetadataColumn(ref outer, snapshot);

            // Column 0x01: Unified account column (accounts, self-destruct, storage)
            WriteAccountColumn(ref outer, snapshot);

            // Column 0x03: State nodes (compact, path length 6-15)
            WriteStateNodesColumnCompact(ref outer, snapshot);

            // Column 0x05: State top nodes (path length 0-5)
            WriteStateTopNodesColumn(ref outer, snapshot);

            // Column 0x06: State nodes fallback (path length 16+)
            WriteStateNodesColumnFallback(ref outer, snapshot);

            // Column 0x07: Storage nodes (compact, path length 6-15)
            WriteStorageNodesColumnCompact(ref outer, snapshot);

            // Column 0x08: Storage nodes fallback (path length 16+)
            WriteStorageNodesColumnFallback(ref outer, snapshot);

            outer.Build();
        }
        finally
        {
            outer.Dispose();
        }
    }

    public static int EstimateSize(Snapshot snapshot)
    {
        // Use a conservative multiplier on the snapshot memory estimate.
        // Clamp to 1 GiB so the buffer stays within ArrayPool's poolable range,
        // and all arithmetic is done in long to avoid int overflow for large snapshots.
        return (int)Math.Min(1.GiB(), snapshot.EstimateMemory() * 3 + 1.KiB());
    }

    private static void WriteMetadataColumn<TWriter>(ref HsstBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Metadata keys must be in sorted order (ASCII): "from_block" < "from_hash" < "to_block" < "to_hash" < "version"
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using HsstBuilder<TWriter> inner = new(ref innerWriter);

        // Use 8-byte little-endian block numbers to avoid stackalloc scope issues
        byte[] blockNumBytes = new byte[8];

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

    private static void WriteAccountColumn<TWriter>(ref HsstBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Collect all unique addresses across accounts, self-destructs, and storages
        // Also build lookup dictionaries for direct access
        SortedSet<byte[]> uniqueAddresses = new(Bytes.Comparer);
        Dictionary<AddressAsKey, Account?> accountLookup = new();
        foreach (KeyValuePair<AddressAsKey, Account?> kv in snapshot.Accounts)
        {
            uniqueAddresses.Add(kv.Key.Value.Bytes.ToArray());
            accountLookup[kv.Key] = kv.Value;
        }
        Dictionary<AddressAsKey, bool> sdLookup = new();
        foreach (KeyValuePair<AddressAsKey, bool> kv in snapshot.SelfDestructedStorageAddresses)
        {
            uniqueAddresses.Add(kv.Key.Value.Bytes.ToArray());
            sdLookup[kv.Key] = kv.Value;
        }
        foreach (KeyValuePair<(AddressAsKey, UInt256), SlotValue?> kv in snapshot.Storages)
            uniqueAddresses.Add(kv.Key.Item1.Value.Bytes.ToArray());

        // Pre-sort storages by (Address, Slot) for efficient iteration
        List<((AddressAsKey Addr, UInt256 Slot) Key, SlotValue? Value)> sortedStorages = new();
        foreach (KeyValuePair<(AddressAsKey, UInt256), SlotValue?> kv in snapshot.Storages)
            sortedStorages.Add((kv.Key, kv.Value));
        sortedStorages.Sort((a, b) =>
        {
            int cmp = a.Key.Addr.Value.Bytes.SequenceCompareTo(b.Key.Addr.Value.Bytes);
            if (cmp != 0) return cmp;
            return a.Key.Slot.CompareTo(b.Key.Slot);
        });

        const int slotPrefixLength = 30;
        const int slotSuffixLength = 2;

        // Address-level HSST
        ref TWriter addressWriter = ref outer.BeginValueWrite();
        using (HsstBuilder<TWriter> addressLevel = new(ref addressWriter, minSeparatorLength: 2))
        {
            byte[] rlpBuffer = new byte[256];
            RlpStream rlpStream = new(rlpBuffer);
            byte[] slotKey = new byte[32];
            int storageIdx = 0;

            foreach (byte[] addrBytes in uniqueAddresses)
            {
                Address address = new(addrBytes);

                // Begin per-address HSST
                ref TWriter perAddrWriter = ref addressLevel.BeginValueWrite();
                using HsstBuilder<TWriter> perAddr = new(ref perAddrWriter);

                // Sub-tag 0x01: Slots
                bool hasStorage = storageIdx < sortedStorages.Count &&
                    sortedStorages[storageIdx].Key.Addr.Value.Bytes.SequenceEqual(addrBytes);
                if (hasStorage)
                {
                    ref TWriter slotWriter = ref perAddr.BeginValueWrite();
                    using HsstBuilder<TWriter> prefixLevel = new(ref slotWriter, minSeparatorLength: 2);

                    while (storageIdx < sortedStorages.Count &&
                        sortedStorages[storageIdx].Key.Addr.Value.Bytes.SequenceEqual(addrBytes))
                    {
                        sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey.AsSpan());
                        byte[] currentPrefix = slotKey[..slotPrefixLength].ToArray();

                        ref TWriter suffixWriter = ref prefixLevel.BeginValueWrite();
                        using HsstBuilder<TWriter> suffixLevel = new(ref suffixWriter, minSeparatorLength: 2, inlineValues: true);

                        while (storageIdx < sortedStorages.Count &&
                            sortedStorages[storageIdx].Key.Addr.Value.Bytes.SequenceEqual(addrBytes))
                        {
                            sortedStorages[storageIdx].Key.Slot.ToBigEndian(slotKey.AsSpan());
                            if (!slotKey.AsSpan(0, slotPrefixLength).SequenceEqual(currentPrefix))
                                break;

                            SlotValue? value = sortedStorages[storageIdx].Value;
                            if (value.HasValue)
                            {
                                ReadOnlySpan<byte> withoutLeadingZeros = value.Value.AsReadOnlySpan.WithoutLeadingZeros();
                                suffixLevel.Add(slotKey.AsSpan(slotPrefixLength, slotSuffixLength), withoutLeadingZeros);
                            }
                            else
                            {
                                suffixLevel.Add(slotKey.AsSpan(slotPrefixLength, slotSuffixLength), ReadOnlySpan<byte>.Empty);
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
                if (sdLookup.TryGetValue(address, out bool sdValue))
                {
                    perAddr.Add(PersistedSnapshot.SelfDestructSubTag, sdValue ? [0x01] : ReadOnlySpan<byte>.Empty);
                }

                // Sub-tag 0x03: Account
                if (accountLookup.TryGetValue(address, out Account? account))
                {
                    if (account is null)
                    {
                        perAddr.Add(PersistedSnapshot.AccountSubTag, ReadOnlySpan<byte>.Empty);
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
                addressLevel.FinishValueWrite(addrBytes);
            }

            addressLevel.Build();
            outer.FinishValueWrite(PersistedSnapshot.AccountColumnTag);
        }
    }

    private static void WriteStateTopNodesColumn<TWriter>(ref HsstBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Sort state nodes with top paths (length 0-5)
        List<(TreePath Path, TrieNode Node)> stateNodes = new();
        foreach (KeyValuePair<TreePath, TrieNode> kv in snapshot.StateNodes)
        {
            if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
            if (kv.Key.Length <= TopPathThreshold)
                stateNodes.Add((kv.Key, kv.Value));
        }
        stateNodes.Sort((a, b) =>
        {
            int cmp = a.Path.Path.Bytes.SequenceCompareTo(b.Path.Path.Bytes);
            if (cmp != 0) return cmp;
            return a.Path.Length.CompareTo(b.Path.Length);
        });

        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using (HsstBuilder<TWriter> inner = new(ref innerWriter, minSeparatorLength: 3))
        {
            byte[] keyBuffer = new byte[3];
            foreach ((TreePath path, TrieNode node) in stateNodes)
            {
                path.EncodeWith3Byte(keyBuffer.AsSpan(0, 3));
                inner.Add(keyBuffer.AsSpan(0, 3), node.FullRlp.Span);
            }

            inner.Build();
            outer.FinishValueWrite(PersistedSnapshot.StateTopNodesTag);
        }
    }

    private static void WriteStateNodesColumnCompact<TWriter>(ref HsstBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Sort state nodes with compact paths (length 6-15)
        List<(TreePath Path, TrieNode Node)> stateNodes = new();
        foreach (KeyValuePair<TreePath, TrieNode> kv in snapshot.StateNodes)
        {
            if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
            if (kv.Key.Length > TopPathThreshold && kv.Key.Length <= CompactPathThreshold)
                stateNodes.Add((kv.Key, kv.Value));
        }
        stateNodes.Sort((a, b) =>
        {
            int cmp = a.Path.Path.Bytes.SequenceCompareTo(b.Path.Path.Bytes);
            if (cmp != 0) return cmp;
            return a.Path.Length.CompareTo(b.Path.Length);
        });

        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using (HsstBuilder<TWriter> inner = new(ref innerWriter, minSeparatorLength: 8))
        {
            byte[] keyBuffer = new byte[8];
            foreach ((TreePath path, TrieNode node) in stateNodes)
            {
                path.EncodeWith8Byte(keyBuffer.AsSpan());
                inner.Add(keyBuffer.AsSpan(0, 8), node.FullRlp.Span);
            }

            inner.Build();
            outer.FinishValueWrite(PersistedSnapshot.StateNodeTag);
        }
    }

    private static void WriteStateNodesColumnFallback<TWriter>(ref HsstBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Sort state nodes with fallback paths (length 16+)
        List<(TreePath Path, TrieNode Node)> stateNodes = new();
        foreach (KeyValuePair<TreePath, TrieNode> kv in snapshot.StateNodes)
        {
            if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
            if (kv.Key.Length > CompactPathThreshold)
                stateNodes.Add((kv.Key, kv.Value));
        }
        stateNodes.Sort((a, b) =>
        {
            int cmp = a.Path.Path.Bytes.SequenceCompareTo(b.Path.Path.Bytes);
            if (cmp != 0) return cmp;
            return a.Path.Length.CompareTo(b.Path.Length);
        });

        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using (HsstBuilder<TWriter> inner = new(ref innerWriter))
        {
            byte[] keyBuffer = new byte[33];
            foreach ((TreePath path, TrieNode node) in stateNodes)
            {
                path.Path.Bytes.CopyTo(keyBuffer.AsSpan());
                keyBuffer[32] = (byte)path.Length;
                inner.Add(keyBuffer.AsSpan(0, 33), node.FullRlp.Span);
            }

            inner.Build();
            outer.FinishValueWrite(PersistedSnapshot.StateNodeFallbackTag);
        }
    }

    private static void WriteStorageNodesColumnCompact<TWriter>(ref HsstBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Sort storage nodes with compact paths (length 0-15)
        List<((Hash256AsKey Addr, TreePath Path) Key, TrieNode Node)> storageNodes = new();
        foreach (KeyValuePair<(Hash256AsKey, TreePath), TrieNode> kv in snapshot.StorageNodes)
        {
            if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
            if (kv.Key.Item2.Length <= CompactPathThreshold)
                storageNodes.Add((kv.Key, kv.Value));
        }
        storageNodes.Sort((a, b) =>
        {
            int cmp = a.Key.Addr.Value.Bytes.SequenceCompareTo(b.Key.Addr.Value.Bytes);
            if (cmp != 0) return cmp;
            cmp = a.Key.Path.Path.Bytes.SequenceCompareTo(b.Key.Path.Path.Bytes);
            if (cmp != 0) return cmp;
            return a.Key.Path.Length.CompareTo(b.Key.Path.Length);
        });

        // Hash-level HSST: Hash256(32) -> inner HSST(TreePath(8) -> NodeRLP)
        ref TWriter hashWriter = ref outer.BeginValueWrite();
        using (HsstBuilder<TWriter> hashLevel = new(ref hashWriter, minSeparatorLength: 2))
        {
            byte[] pathKey = new byte[8];
            int i = 0;
            while (i < storageNodes.Count)
            {
                Hash256 currentHash = storageNodes[i].Key.Addr;

                ref TWriter innerWriter = ref hashLevel.BeginValueWrite();
                using HsstBuilder<TWriter> inner = new(ref innerWriter, minSeparatorLength: 8);

                while (i < storageNodes.Count && storageNodes[i].Key.Addr.Equals(currentHash))
                {
                    ((Hash256AsKey _, TreePath path) snKey, TrieNode node) = storageNodes[i];
                    snKey.path.EncodeWith8Byte(pathKey.AsSpan());
                    inner.Add(pathKey.AsSpan(0, 8), node.FullRlp.Span);
                    i++;
                }

                inner.Build();
                hashLevel.FinishValueWrite(currentHash.Bytes[..StorageHashPrefixLength]);
            }

            hashLevel.Build();
            outer.FinishValueWrite(PersistedSnapshot.StorageNodeTag);
        }
    }

    private static void WriteStorageNodesColumnFallback<TWriter>(ref HsstBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Sort storage nodes with fallback paths (length 16+)
        List<((Hash256AsKey Addr, TreePath Path) Key, TrieNode Node)> storageNodes = new();
        foreach (KeyValuePair<(Hash256AsKey, TreePath), TrieNode> kv in snapshot.StorageNodes)
        {
            if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
            if (kv.Key.Item2.Length > CompactPathThreshold)
                storageNodes.Add((kv.Key, kv.Value));
        }
        storageNodes.Sort((a, b) =>
        {
            int cmp = a.Key.Addr.Value.Bytes.SequenceCompareTo(b.Key.Addr.Value.Bytes);
            if (cmp != 0) return cmp;
            cmp = a.Key.Path.Path.Bytes.SequenceCompareTo(b.Key.Path.Path.Bytes);
            if (cmp != 0) return cmp;
            return a.Key.Path.Length.CompareTo(b.Key.Path.Length);
        });

        // Hash-level HSST: Hash256(32) -> inner HSST(TreePath(33) -> NodeRLP)
        ref TWriter hashWriter = ref outer.BeginValueWrite();
        using (HsstBuilder<TWriter> hashLevel = new(ref hashWriter, minSeparatorLength: 2))
        {
            byte[] pathKey = new byte[33];
            int i = 0;
            while (i < storageNodes.Count)
            {
                Hash256 currentHash = storageNodes[i].Key.Addr;

                ref TWriter innerWriter = ref hashLevel.BeginValueWrite();
                using HsstBuilder<TWriter> inner = new(ref innerWriter);

                while (i < storageNodes.Count && storageNodes[i].Key.Addr.Equals(currentHash))
                {
                    ((Hash256AsKey _, TreePath path) snKey, TrieNode node) = storageNodes[i];
                    snKey.path.Path.Bytes.CopyTo(pathKey.AsSpan());
                    pathKey[32] = (byte)snKey.path.Length;
                    inner.Add(pathKey.AsSpan(0, 33), node.FullRlp.Span);
                    i++;
                }

                inner.Build();
                hashLevel.FinishValueWrite(currentHash.Bytes[..StorageHashPrefixLength]);
            }

            hashLevel.Build();
            outer.FinishValueWrite(PersistedSnapshot.StorageNodeFallbackTag);
        }
    }

    /// <summary>
    /// Convert a Full snapshot into a Linked snapshot where trie RLP columns have NodeRefs.
    /// Account column (0x01) is copied as-is. Metadata column (0x00) is copied as-is.
    /// Trie columns (0x03, 0x05, 0x06) have values replaced with NodeRef(snapshotId, offset).
    /// Nested trie columns (0x07, 0x08) have inner values replaced with NodeRefs.
    /// </summary>
    internal static void ConvertFullToLinked<TWriter>(PersistedSnapshot fullSnapshot, ref TWriter writer) where TWriter : IByteBufferWriter
    {
        ReadOnlySpan<byte> snapshotData = fullSnapshot.GetSpan();
        Hsst.Hsst outer = new(snapshotData);
        using HsstBuilder<TWriter> outerBuilder = new(ref writer);

        byte[][] tags = [
            PersistedSnapshot.MetadataTag,
            PersistedSnapshot.AccountColumnTag,
            PersistedSnapshot.StateNodeTag,
            PersistedSnapshot.StateTopNodesTag,
            PersistedSnapshot.StateNodeFallbackTag,
            PersistedSnapshot.StorageNodeTag,
            PersistedSnapshot.StorageNodeFallbackTag,
        ];

        int snapshotId = fullSnapshot.Id;

        foreach (byte[] tag in tags)
        {
            if (!outer.TryGet(tag, out ReadOnlySpan<byte> column)) continue;
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

    private static void CopyColumn<TWriter>(ReadOnlySpan<byte> column, ref TWriter writer) where TWriter : IByteBufferWriter
    {
        column.CopyTo(writer.GetSpan(column.Length));
        writer.Advance(column.Length);
    }

    /// <summary>
    /// Convert a flat (non-nested) trie column's values to NodeRefs.
    /// Each entry's RLP value is replaced with a NodeRef pointing back to the Full snapshot.
    /// </summary>
    private static void ConvertFlatColumnToNodeRefs<TWriter>(
        ReadOnlySpan<byte> column, ref TWriter writer,
        int snapshotId, int columnOffset,
        int minSeparatorLength = 0) where TWriter : IByteBufferWriter
    {
        Hsst.Hsst hsst = new(column);
        HsstBuilder<TWriter> builder = new(ref writer, minSeparatorLength, inlineValues: true);
        Hsst.Hsst.Enumerator e = hsst.GetEnumerator();
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];

        while (e.MoveNext())
        {
            NodeRef.Write(refBytes, new NodeRef(snapshotId, columnOffset + e.CurrentMetadataStart));
            builder.Add(e.Current.Key, refBytes);
        }

        builder.Build();
        builder.Dispose();
        e.Dispose();
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
        Hsst.Hsst outerHsst = new(column);
        HsstBuilder<TWriter> builder = new(ref writer, outerMinSep);
        Hsst.Hsst.Enumerator outerEnum = outerHsst.GetEnumerator();
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];

        while (outerEnum.MoveNext())
        {
            ReadOnlySpan<byte> innerData = outerEnum.Current.Value;
            int innerOffset = SpanOffset(snapshotData, innerData);

            Hsst.Hsst innerHsst = new(innerData);
            ref TWriter innerWriter = ref builder.BeginValueWrite();
            HsstBuilder<TWriter> innerBuilder = new(ref innerWriter, innerMinSep, inlineValues: true);
            Hsst.Hsst.Enumerator innerEnum = innerHsst.GetEnumerator();

            while (innerEnum.MoveNext())
            {
                NodeRef.Write(refBytes, new NodeRef(snapshotId, innerOffset + innerEnum.CurrentMetadataStart));
                innerBuilder.Add(innerEnum.Current.Key, refBytes);
            }

            innerBuilder.Build();
            innerBuilder.Dispose();
            innerEnum.Dispose();
            builder.FinishValueWrite(outerEnum.Current.Key);
        }

        builder.Build();
        builder.Dispose();
        outerEnum.Dispose();
    }

    /// <summary>
    /// N-way merge of N persisted snapshots (oldest-first) into output buffer.
    /// Pre-converts all Full snapshots to Linked so the merge only handles Linked snapshots
    /// (all trie values are already NodeRefs). This eliminates the dual code path in trie merges.
    /// </summary>
    internal static void NWayMergeSnapshots<TWriter>(PersistedSnapshotList snapshots, ref TWriter writer, HashSet<int> referencedIds) where TWriter : IByteBufferWriter
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
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(estimatedSize, snapshots[i].Size));
                    try
                    {
                        SpanBufferWriter convertWriter = new(buffer);
                        ConvertFullToLinked(snapshots[i], ref convertWriter);
                        SnapshotLocation loc = tempArena.Allocate(buffer.AsSpan(0, convertWriter.Written));
                        ArenaReservation tempRes = tempArena.Open(loc);
                        PersistedSnapshot convertedSnap = new(snapshots[i].Id, snapshots[i].From, snapshots[i].To,
                            PersistedSnapshotType.Linked, tempRes);
                        mergeSnapshots.Add(convertedSnap);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                else
                {
                    if (!snapshots[i].TryAcquire())
                        throw new InvalidOperationException("Cannot acquire lease for snapshot");
                    mergeSnapshots.Add(snapshots[i]);
                }
            }

            using HsstBuilder<TWriter> outerBuilder = new(ref writer);

            byte[][] tags = [
                PersistedSnapshot.MetadataTag,
                PersistedSnapshot.AccountColumnTag,
                PersistedSnapshot.StateNodeTag,
                PersistedSnapshot.StateTopNodesTag,
                PersistedSnapshot.StateNodeFallbackTag,
                PersistedSnapshot.StorageNodeTag,
                PersistedSnapshot.StorageNodeFallbackTag,
            ];

            foreach (byte[] tag in tags)
            {
                ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();

                // All trie columns now use NWayStreamingMerge since all inputs are Linked (values are NodeRefs)
                switch (tag[0])
                {
                    case 0x00:
                        NWayMetadataMerge(snapshots, ref valueWriter, referencedIds);
                        break;
                    case 0x01:
                        NWayMergeAccountColumn(mergeSnapshots, tag, ref valueWriter);
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
    /// Uses <see cref="Hsst.Hsst.MergeEnumerator"/> for zero-allocation cursor-based enumeration.
    /// </summary>
    internal static void NWayStreamingMerge<TWriter>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer,
        int minSeparatorLength = 0, bool inlineValues = false) where TWriter : IByteBufferWriter
    {
        int n = snapshots.Count;
        Hsst.Hsst.MergeEnumerator[] enums = new Hsst.Hsst.MergeEnumerator[n];
        bool[] hasMore = new bool[n];

        try
        {
            for (int i = 0; i < n; i++)
            {
                ReadOnlySpan<byte> snapshotData = snapshots[i].GetSpan();
                Hsst.Hsst outer = new(snapshotData);
                outer.TryGet(tag, out ReadOnlySpan<byte> column);
                enums[i] = new Hsst.Hsst.MergeEnumerator(column, isInline: inlineValues);
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

                // Get column span for the winner to read its value
                ReadOnlySpan<byte> winnerSnapshotData = snapshots[minIdx].GetSpan();
                Hsst.Hsst winnerOuter = new(winnerSnapshotData);
                winnerOuter.TryGet(tag, out ReadOnlySpan<byte> winnerColumn);
                builder.Add(minKey, enums[minIdx].GetCurrentValue(winnerColumn));

                // Advance all enumerators that had the min key.
                // Advance minIdx LAST because minKey references its _keyBuffer which MoveNext overwrites.
                for (int i = 0; i < n; i++)
                {
                    if (i == minIdx || !hasMore[i]) continue;
                    if (enums[i].CurrentKey.SequenceCompareTo(minKey) == 0)
                    {
                        ReadOnlySpan<byte> sd = snapshots[i].GetSpan();
                        Hsst.Hsst so = new(sd);
                        so.TryGet(tag, out ReadOnlySpan<byte> col);
                        hasMore[i] = enums[i].MoveNext(col);
                    }
                }
                {
                    ReadOnlySpan<byte> sd = snapshots[minIdx].GetSpan();
                    Hsst.Hsst so = new(sd);
                    so.TryGet(tag, out ReadOnlySpan<byte> col);
                    hasMore[minIdx] = enums[minIdx].MoveNext(col);
                }
            }

            builder.Build();
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i]?.Dispose();
        }
    }

    /// <summary>
    /// N-way nested streaming merge: outer keys merged across N sources,
    /// when M sources share an outer key their inner HSST values are merged via NWayStreamingMerge.
    /// Single-source keys are copied as-is.
    /// </summary>
    internal static void NWayNestedStreamingMerge<TWriter>(
        Hsst.Hsst.MergeEnumerator[] enums, bool[] hasMore, int n,
        Func<int, ReadOnlySpan<byte>> getColumnSpan,
        ref TWriter writer,
        int outerMinSep = 0, int innerMinSep = 0, bool innerInline = false) where TWriter : IByteBufferWriter
    {
        using HsstBuilder<TWriter> builder = new(ref writer, outerMinSep);

        // Temp array for collecting matching source indices
        int[] matchingSources = new int[n];

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
                builder.Add(minKey, enums[srcIdx].GetCurrentValue(getColumnSpan(srcIdx)));
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
        Hsst.Hsst.MergeEnumerator[] outerEnums, int[] matchingSources, int matchCount,
        Func<int, ReadOnlySpan<byte>> getColumnSpan,
        ref TWriter writer,
        int minSeparatorLength = 0, bool inlineValues = false) where TWriter : IByteBufferWriter
    {
        Hsst.Hsst.MergeEnumerator[] innerEnums = new Hsst.Hsst.MergeEnumerator[matchCount];
        bool[] innerHasMore = new bool[matchCount];
        // Store inner data as byte[] since we can't keep ReadOnlySpan<byte> in arrays
        byte[][] innerData = new byte[matchCount][];

        try
        {
            for (int j = 0; j < matchCount; j++)
            {
                int srcIdx = matchingSources[j];
                ReadOnlySpan<byte> value = outerEnums[srcIdx].GetCurrentValue(getColumnSpan(srcIdx));
                innerData[j] = value.ToArray();
                innerEnums[j] = new Hsst.Hsst.MergeEnumerator(innerData[j], isInline: inlineValues);
                innerHasMore[j] = innerEnums[j].MoveNext(innerData[j]);
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
                builder.Add(minKey, innerEnums[minIdx].GetCurrentValue(innerData[minIdx]));

                // Advance all with min key.
                // Advance minIdx LAST because minKey references its _keyBuffer which MoveNext overwrites.
                for (int j = 0; j < matchCount; j++)
                {
                    if (j == minIdx || !innerHasMore[j]) continue;
                    if (innerEnums[j].CurrentKey.SequenceCompareTo(minKey) == 0)
                        innerHasMore[j] = innerEnums[j].MoveNext(innerData[j]);
                }
                innerHasMore[minIdx] = innerEnums[minIdx].MoveNext(innerData[minIdx]);
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
        Hsst.Hsst.MergeEnumerator[] enums = new Hsst.Hsst.MergeEnumerator[n];
        bool[] hasMore = new bool[n];

        try
        {
            for (int i = 0; i < n; i++)
            {
                ReadOnlySpan<byte> snapshotData = snapshots[i].GetSpan();
                Hsst.Hsst outer = new(snapshotData);
                outer.TryGet(tag, out ReadOnlySpan<byte> column);
                enums[i] = new Hsst.Hsst.MergeEnumerator(column, isInline: false);
                hasMore[i] = enums[i].MoveNext(column);
            }

            NWayNestedStreamingMerge(enums, hasMore, n,
                i =>
                {
                    ReadOnlySpan<byte> sd = snapshots[i].GetSpan();
                    Hsst.Hsst so = new(sd);
                    so.TryGet(tag, out ReadOnlySpan<byte> col);
                    return col;
                },
                ref writer, outerMinSep, innerMinSep, innerInline);
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i]?.Dispose();
        }
    }

    /// <summary>
    /// N-way merge of the account column (tag 0x01) across N snapshots.
    /// Outer: 20-byte address keys (minSep=2). For matching addresses with M sources,
    /// calls <see cref="NWayMergePerAddressHsst"/>. Single source: copy as-is.
    /// </summary>
    internal static void NWayMergeAccountColumn<TWriter>(
        PersistedSnapshotList snapshots, byte[] tag, ref TWriter writer) where TWriter : IByteBufferWriter
    {
        int n = snapshots.Count;
        Hsst.Hsst.MergeEnumerator[] enums = new Hsst.Hsst.MergeEnumerator[n];
        bool[] hasMore = new bool[n];

        try
        {
            for (int i = 0; i < n; i++)
            {
                ReadOnlySpan<byte> snapshotData = snapshots[i].GetSpan();
                Hsst.Hsst outer = new(snapshotData);
                outer.TryGet(tag, out ReadOnlySpan<byte> column);
                enums[i] = new Hsst.Hsst.MergeEnumerator(column, isInline: false);
                hasMore[i] = enums[i].MoveNext(column);
            }

            using HsstBuilder<TWriter> builder = new(ref writer, minSeparatorLength: 2);
            int[] matchingSources = new int[n];

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
                    ReadOnlySpan<byte> sd = snapshots[srcIdx].GetSpan();
                    Hsst.Hsst so = new(sd);
                    so.TryGet(tag, out ReadOnlySpan<byte> col);
                    builder.Add(minKey, enums[srcIdx].GetCurrentValue(col));
                }
                else
                {
                    // M sources share this address: merge per-address HSSTs
                    ref TWriter perAddrWriter = ref builder.BeginValueWrite();
                    NWayMergePerAddressHsst(
                        enums, matchingSources, matchCount, snapshots, tag,
                        ref perAddrWriter);
                    builder.FinishValueWrite(minKey);
                }

                for (int j = 0; j < matchCount; j++)
                {
                    int i = matchingSources[j];
                    ReadOnlySpan<byte> sd = snapshots[i].GetSpan();
                    Hsst.Hsst so = new(sd);
                    so.TryGet(tag, out ReadOnlySpan<byte> col);
                    hasMore[i] = enums[i].MoveNext(col);
                }
            }

            builder.Build();
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i]?.Dispose();
        }
    }

    /// <summary>
    /// N-way merge of per-address HSSTs from M sources (oldest-first by matchingSources order).
    /// - Slots: find newest destruct barrier, merge slots from barrier..M-1 via nested streaming merge
    /// - SelfDestruct: iterate 0..M-1, apply TryAdd semantics
    /// - Account: newest wins (walk M-1..0, first with AccountSubTag)
    /// </summary>
    private static void NWayMergePerAddressHsst<TWriter>(
        Hsst.Hsst.MergeEnumerator[] outerEnums, int[] matchingSources, int matchCount,
        PersistedSnapshotList snapshots, byte[] tag,
        ref TWriter writer) where TWriter : IByteBufferWriter
    {
        // Get per-address HSST data for each matching source
        byte[][] perAddrData = new byte[matchCount][];
        for (int j = 0; j < matchCount; j++)
        {
            int srcIdx = matchingSources[j];
            ReadOnlySpan<byte> sd = snapshots[srcIdx].GetSpan();
            Hsst.Hsst outer = new(sd);
            outer.TryGet(tag, out ReadOnlySpan<byte> col);
            perAddrData[j] = outerEnums[srcIdx].GetCurrentValue(col).ToArray();
        }

        using HsstBuilder<TWriter> perAddrBuilder = new(ref writer);

        // Find newest destruct barrier: newest j where SelfDestructSubTag value is empty (destructed)
        int destructBarrier = -1;
        for (int j = 0; j < matchCount; j++)
        {
            Hsst.Hsst h = new(perAddrData[j]);
            if (h.TryGet(PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> sdVal) && sdVal.IsEmpty)
                destructBarrier = j;
        }

        // Sub-tag 0x01: Slots
        // Merge slots only from max(0, destructBarrier)..matchCount-1
        int slotStart = Math.Max(0, destructBarrier);
        {
            // Collect sources that have slots in the range
            int slotSourceCount = 0;
            int[] slotSources = new int[matchCount - slotStart];
            byte[][] slotData = new byte[matchCount - slotStart][];
            for (int j = slotStart; j < matchCount; j++)
            {
                Hsst.Hsst h = new(perAddrData[j]);
                if (h.TryGet(PersistedSnapshot.SlotSubTag, out ReadOnlySpan<byte> slots))
                {
                    slotSources[slotSourceCount] = j;
                    slotData[slotSourceCount] = slots.ToArray();
                    slotSourceCount++;
                }
            }

            if (slotSourceCount == 1)
            {
                perAddrBuilder.Add(PersistedSnapshot.SlotSubTag, slotData[0]);
            }
            else if (slotSourceCount > 1)
            {
                // N-way nested streaming merge on slot prefix-level HSSTs
                Hsst.Hsst.MergeEnumerator[] slotEnums = new Hsst.Hsst.MergeEnumerator[slotSourceCount];
                bool[] slotHasMore = new bool[slotSourceCount];
                try
                {
                    for (int j = 0; j < slotSourceCount; j++)
                    {
                        slotEnums[j] = new Hsst.Hsst.MergeEnumerator(slotData[j], isInline: false);
                        slotHasMore[j] = slotEnums[j].MoveNext(slotData[j]);
                    }

                    ref TWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                    NWayNestedStreamingMerge(
                        slotEnums, slotHasMore, slotSourceCount,
                        j => slotData[j].AsSpan(),
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
                Hsst.Hsst h = new(perAddrData[j]);
                if (!h.TryGet(PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> sdVal)) continue;

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
                        sdResult = ReadOnlySpan<byte>.Empty;
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
                Hsst.Hsst h = new(perAddrData[j]);
                if (h.TryGet(PersistedSnapshot.AccountSubTag, out ReadOnlySpan<byte> account))
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
        ReadOnlySpan<byte> oldestData = snapshots[0].GetSpan();
        ReadOnlySpan<byte> newestData = snapshots[n - 1].GetSpan();

        Hsst.Hsst oldestOuter = new(oldestData);
        Hsst.Hsst newestOuter = new(newestData);
        oldestOuter.TryGet(PersistedSnapshot.MetadataTag, out ReadOnlySpan<byte> oldestMeta);
        newestOuter.TryGet(PersistedSnapshot.MetadataTag, out ReadOnlySpan<byte> newestMeta);

        Hsst.Hsst oldestHsst = new(oldestMeta);
        Hsst.Hsst newestHsst = new(newestMeta);

        // Extract fields
        oldestHsst.TryGet("from_block"u8, out ReadOnlySpan<byte> fromBlock);
        oldestHsst.TryGet("from_hash"u8, out ReadOnlySpan<byte> fromHash);
        newestHsst.TryGet("to_block"u8, out ReadOnlySpan<byte> toBlock);
        newestHsst.TryGet("to_hash"u8, out ReadOnlySpan<byte> toHash);
        newestHsst.TryGet("version"u8, out ReadOnlySpan<byte> version);

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
}
