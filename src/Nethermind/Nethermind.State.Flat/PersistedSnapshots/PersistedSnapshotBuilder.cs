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
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Builds columnar HSST byte data from an in-memory <see cref="Snapshot"/>.
/// The outer HSST has 7 column entries, each containing an inner HSST.
/// Inner HSST keys are the entity keys without the tag prefix.
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

    /// <summary>
    /// Convenience method: allocate output buffer and build.
    /// </summary>
    public static byte[] Build(Snapshot snapshot)
    {
        int estimatedSize = EstimateSize(snapshot);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(estimatedSize);
        try
        {
            SpanBufferWriter writer = new(buffer);
            Build(snapshot, ref writer);
            return buffer.AsSpan(0, writer.Written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

    // --- Merge/compaction methods (moved from PersistedSnapshotCompactor) ---

    /// <summary>
    /// Merge a list of persisted snapshots (oldest-first) into a single compacted byte[].
    /// Uses pairwise self-destruct-aware merge from oldest to newest.
    /// </summary>
    internal static byte[] MergeSnapshots(PersistedSnapshotList snapshots)
    {
        if (snapshots.Count == 0) throw new ArgumentException("Cannot merge empty snapshot list");
        if (snapshots.Count == 1) return snapshots[0].GetSpan().ToArray();

        // Collect all base snapshot IDs for metadata
        HashSet<int> referencedIds = new();
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (snapshots[i].Type == PersistedSnapshotType.Base)
                referencedIds.Add(snapshots[i].Id);
            else if (snapshots[i].ReferencedSnapshotIds is int[] ids)
            {
                for (int j = 0; j < ids.Length; j++) referencedIds.Add(ids[j]);
            }
        }

        int totalSize = 0;
        for (int i = 0; i < snapshots.Count; i++) totalSize += snapshots[i].Size;
        totalSize += 4096;

        byte[] bufA = ArrayPool<byte>.Shared.Rent(totalSize);
        byte[] bufB = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            int len = MergeSnapshots(snapshots, bufA, bufB, out bool resultInA, referencedIds);
            return (resultInA ? bufA : bufB).AsSpan(0, len).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufA);
            ArrayPool<byte>.Shared.Return(bufB);
        }
    }

    internal static int MergeSnapshots(PersistedSnapshotList snapshots, Span<byte> bufferA, Span<byte> bufferB, out bool resultInA,
        HashSet<int> referencedIds)
    {
        // Reversed: start with newest, iterate backward — smaller intermediates early
        int last = snapshots.Count - 1;
        snapshots[last].GetSpan().CopyTo(bufferA);
        int currentLen = snapshots[last].Size;
        int newerSnapshotId = snapshots[last].Id;
        bool newerHasNodeRefs = snapshots[last].Type == PersistedSnapshotType.Compacted;
        resultInA = true;

        for (int i = last - 1; i >= 0; i--)
        {
            bool olderHasNodeRefs = snapshots[i].Type == PersistedSnapshotType.Compacted;
            ReadOnlySpan<byte> newerSrc = resultInA ? bufferA[..currentLen] : bufferB[..currentLen];
            Span<byte> dst = resultInA ? bufferB : bufferA;
            currentLen = MergeTwoPersisted(snapshots[i].GetSpan(), newerSrc, dst,
                snapshots[i].Id, newerSnapshotId,
                olderHasNodeRefs, newerHasNodeRefs,
                referencedIds);
            // After first merge, accumulator (newer side) always has NodeRefs
            newerHasNodeRefs = true;
            newerSnapshotId = 0;
            resultInA = !resultInA;
        }

        return currentLen;
    }

    /// <summary>
    /// Merge two columnar HSST snapshots.
    ///   - Account column (0x01): unified per-address merge with self-destruct awareness
    ///   - Trie node columns (0x03,0x05,0x06): emit NodeRef instead of copying RLP
    ///   - StorageNodes columns (0x07,0x08): nested merge with NodeRef for inner values
    /// </summary>
    private static int MergeTwoPersisted(ReadOnlySpan<byte> olderData, ReadOnlySpan<byte> newerData, Span<byte> output,
        int olderSnapshotId, int newerSnapshotId,
        bool olderHasNodeRefs, bool newerHasNodeRefs,
        HashSet<int> referencedIds)
    {
        Hsst.Hsst olderOuter = new(olderData);
        Hsst.Hsst newerOuter = new(newerData);

        SpanBufferWriter outerWriter = new(output);
        using HsstBuilder<SpanBufferWriter> outerBuilder = new(ref outerWriter);
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
            bool hasOlder = olderOuter.TryGet(tag, out ReadOnlySpan<byte> olderColumn);
            bool hasNewer = newerOuter.TryGet(tag, out ReadOnlySpan<byte> newerColumn);
            Debug.Assert(hasOlder && hasNewer, $"Missing required tag 0x{tag[0]:X2} in persisted snapshot");

            ref SpanBufferWriter valueWriter = ref outerBuilder.BeginValueWrite();

            int columnLen = tag[0] switch
            {
                0x00 => MetadataMergeWithCompactedFlags(olderColumn, newerColumn, valueWriter.GetSpan(0), referencedIds),
                0x01 => MergeAccountColumn(olderColumn, newerColumn, valueWriter.GetSpan(0)),
                0x03 => StreamingMergeWithNodeRef(olderColumn, newerColumn, valueWriter.GetSpan(0),
                    olderSnapshotId, olderData, newerSnapshotId, newerData,
                    olderHasNodeRefs, newerHasNodeRefs, minSeparatorLength: 8, inlineValues: true),
                0x05 => StreamingMergeWithNodeRef(olderColumn, newerColumn, valueWriter.GetSpan(0),
                    olderSnapshotId, olderData, newerSnapshotId, newerData,
                    olderHasNodeRefs, newerHasNodeRefs, minSeparatorLength: 3, inlineValues: true),
                0x06 => StreamingMergeWithNodeRef(olderColumn, newerColumn, valueWriter.GetSpan(0),
                    olderSnapshotId, olderData, newerSnapshotId, newerData,
                    olderHasNodeRefs, newerHasNodeRefs),
                0x07 => NestedStreamingMergeWithNodeRef(olderColumn, newerColumn, valueWriter.GetSpan(0),
                    olderSnapshotId, olderData, newerSnapshotId, newerData,
                    olderHasNodeRefs, newerHasNodeRefs, outerMinSep: 2, innerMinSep: 8, innerInline: true),
                0x08 => NestedStreamingMergeWithNodeRef(olderColumn, newerColumn, valueWriter.GetSpan(0),
                    olderSnapshotId, olderData, newerSnapshotId, newerData,
                    olderHasNodeRefs, newerHasNodeRefs, outerMinSep: 2),
                _ => StreamingMerge(olderColumn, newerColumn, valueWriter.GetSpan(0), 0),
            };

            valueWriter.Advance(columnLen);
            outerBuilder.FinishValueWrite(tag);
        }

        outerBuilder.Build();
        return outerWriter.Written;
    }

    /// <summary>
    /// Merge self-destruct columns with TryAdd semantics:
    ///   - newer=empty (destructed) -> always empty
    ///   - newer=0x01 (new account) -> use older value if exists, else 0x01
    /// </summary>
    internal static int SelfDestructMerge(ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output, int minSeparatorLength = 0)
    {
        Hsst.Hsst olderHsst = new(older);
        Hsst.Hsst newerHsst = new(newer);

        if (olderHsst.EntryCount == 0 && newerHsst.EntryCount == 0)
        {
            // Return minimal empty HSST (version byte + empty index)
            SpanBufferWriter emptyWriter = new(output);
            using HsstBuilder<SpanBufferWriter> empty = new(ref emptyWriter);
            empty.Build();
            return emptyWriter.Written;
        }

        SpanBufferWriter writer = new(output);
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer, minSeparatorLength);
        using Hsst.Hsst.Enumerator olderEnum = olderHsst.GetEnumerator();
        using Hsst.Hsst.Enumerator newerEnum = newerHsst.GetEnumerator();

        bool hasOlder = olderEnum.MoveNext();
        bool hasNewer = newerEnum.MoveNext();

        while (hasOlder && hasNewer)
        {
            ReadOnlySpan<byte> olderKey = olderEnum.Current.Key;
            ReadOnlySpan<byte> newerKey = newerEnum.Current.Key;

            int cmp = olderKey.SequenceCompareTo(newerKey);
            if (cmp < 0)
            {
                builder.Add(olderKey, olderEnum.Current.Value);
                hasOlder = olderEnum.MoveNext();
            }
            else if (cmp > 0)
            {
                builder.Add(newerKey, newerEnum.Current.Value);
                hasNewer = newerEnum.MoveNext();
            }
            else
            {
                // Keys match: newer=empty -> empty, newer=0x01 -> use older (TryAdd)
                builder.Add(newerKey, newerEnum.Current.Value.IsEmpty
                    ? ReadOnlySpan<byte>.Empty
                    : olderEnum.Current.Value);
                hasOlder = olderEnum.MoveNext();
                hasNewer = newerEnum.MoveNext();
            }
        }

        while (hasOlder)
        {
            builder.Add(olderEnum.Current.Key, olderEnum.Current.Value);
            hasOlder = olderEnum.MoveNext();
        }

        while (hasNewer)
        {
            builder.Add(newerEnum.Current.Key, newerEnum.Current.Value);
            hasNewer = newerEnum.MoveNext();
        }

        builder.Build();
        return writer.Written;
    }

    /// <summary>
    /// Merge unified account columns. Each address maps to a per-address HSST with sub-tags:
    ///   0x01 (SlotSubTag): nested storage HSST
    ///   0x02 (SelfDestructSubTag): SD flag
    ///   0x03 (AccountSubTag): account RLP
    /// Merge rules per sub-tag:
    ///   SlotSubTag: if newer has SelfDestructSubTag with empty value (destructed), use newer slots only; otherwise nested merge
    ///   SelfDestructSubTag: TryAdd semantics (newer=empty→empty, newer=0x01→older if exists)
    ///   AccountSubTag: newer wins
    /// </summary>
    internal static int MergeAccountColumn(ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output)
    {
        Hsst.Hsst olderHsst = new(older);
        Hsst.Hsst newerHsst = new(newer);

        SpanBufferWriter writer = new(output);
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer, minSeparatorLength: 2);
        using Hsst.Hsst.Enumerator olderEnum = olderHsst.GetEnumerator();
        using Hsst.Hsst.Enumerator newerEnum = newerHsst.GetEnumerator();

        bool hasOlder = olderEnum.MoveNext();
        bool hasNewer = newerEnum.MoveNext();

        while (hasOlder && hasNewer)
        {
            ReadOnlySpan<byte> olderKey = olderEnum.Current.Key;
            ReadOnlySpan<byte> newerKey = newerEnum.Current.Key;

            int cmp = olderKey.SequenceCompareTo(newerKey);
            if (cmp < 0)
            {
                builder.Add(olderKey, olderEnum.Current.Value);
                hasOlder = olderEnum.MoveNext();
            }
            else if (cmp > 0)
            {
                builder.Add(newerKey, newerEnum.Current.Value);
                hasNewer = newerEnum.MoveNext();
            }
            else
            {
                // Same address: merge per-address sub-HSSTs
                ref SpanBufferWriter perAddrWriter = ref builder.BeginValueWrite();
                int len = MergePerAddressHsst(olderEnum.Current.Value, newerEnum.Current.Value, perAddrWriter.GetSpan(0));
                perAddrWriter.Advance(len);
                builder.FinishValueWrite(newerKey);
                hasOlder = olderEnum.MoveNext();
                hasNewer = newerEnum.MoveNext();
            }
        }

        while (hasOlder)
        {
            builder.Add(olderEnum.Current.Key, olderEnum.Current.Value);
            hasOlder = olderEnum.MoveNext();
        }

        while (hasNewer)
        {
            builder.Add(newerEnum.Current.Key, newerEnum.Current.Value);
            hasNewer = newerEnum.MoveNext();
        }

        builder.Build();
        return writer.Written;
    }

    /// <summary>
    /// Merge two per-address sub-HSSTs (sub-tags 0x01, 0x02, 0x03).
    /// </summary>
    private static int MergePerAddressHsst(ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output)
    {
        Hsst.Hsst olderHsst = new(older);
        Hsst.Hsst newerHsst = new(newer);

        // Check if newer has self-destruct with empty value (address was destructed)
        bool isDestructed = newerHsst.TryGet(PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> newerSdValue) && newerSdValue.IsEmpty;

        SpanBufferWriter writer = new(output);
        using HsstBuilder<SpanBufferWriter> perAddrBuilder = new(ref writer);

        // Sub-tag 0x01: Slots
        bool olderHasSlots = olderHsst.TryGet(PersistedSnapshot.SlotSubTag, out ReadOnlySpan<byte> olderSlots);
        bool newerHasSlots = newerHsst.TryGet(PersistedSnapshot.SlotSubTag, out ReadOnlySpan<byte> newerSlots);
        if (olderHasSlots || newerHasSlots)
        {
            if (isDestructed || !olderHasSlots)
            {
                // Destructed or only newer has slots: use newer only
                if (newerHasSlots)
                    perAddrBuilder.Add(PersistedSnapshot.SlotSubTag, newerSlots);
            }
            else if (!newerHasSlots)
            {
                // Only older has slots
                perAddrBuilder.Add(PersistedSnapshot.SlotSubTag, olderSlots);
            }
            else
            {
                // Both have slots, not destructed: merge prefix-level HSSTs
                ref SpanBufferWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                int slotLen = NestedStreamingMerge(olderSlots, newerSlots, slotWriter.GetSpan(0),
                    outerMinSep: 2, innerMinSep: 2, innerInline: true);
                slotWriter.Advance(slotLen);
                perAddrBuilder.FinishValueWrite(PersistedSnapshot.SlotSubTag);
            }
        }

        // Sub-tag 0x02: Self-destruct
        bool olderHasSd = olderHsst.TryGet(PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> olderSdValue);
        if (isDestructed)
        {
            // newer=empty (destructed) → always empty
            perAddrBuilder.Add(PersistedSnapshot.SelfDestructSubTag, ReadOnlySpan<byte>.Empty);
        }
        else if (newerHsst.TryGet(PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> newerSdVal))
        {
            // newer=0x01 (new account) → use older if exists (TryAdd)
            perAddrBuilder.Add(PersistedSnapshot.SelfDestructSubTag, olderHasSd ? olderSdValue : newerSdVal);
        }
        else if (olderHasSd)
        {
            perAddrBuilder.Add(PersistedSnapshot.SelfDestructSubTag, olderSdValue);
        }

        // Sub-tag 0x03: Account — newer wins
        bool olderHasAccount = olderHsst.TryGet(PersistedSnapshot.AccountSubTag, out ReadOnlySpan<byte> olderAccount);
        bool newerHasAccount = newerHsst.TryGet(PersistedSnapshot.AccountSubTag, out ReadOnlySpan<byte> newerAccount);
        if (newerHasAccount)
            perAddrBuilder.Add(PersistedSnapshot.AccountSubTag, newerAccount);
        else if (olderHasAccount)
            perAddrBuilder.Add(PersistedSnapshot.AccountSubTag, olderAccount);

        perAddrBuilder.Build();
        return writer.Written;
    }

    /// <summary>
    /// Like <see cref="NestedStreamingMerge"/> but skips older storage for destructed addresses.
    /// When address is destructed:
    ///   - Key in both: use newer only (don't merge inner HSSTs)
    ///   - Key only in older: skip entirely
    ///   - Key only in newer: include (new storage after self-destruct)
    /// </summary>
    internal static int NestedStreamingMergeWithSelfDestruct(
        ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output,
        HashSet<byte[]> destructedAddresses,
        int outerMinSep = 0, int innerMinSep = 0, bool innerInline = false)
    {
        Hsst.Hsst olderHsst = new(older);
        Hsst.Hsst newerHsst = new(newer);

        if (olderHsst.EntryCount == 0 && newerHsst.EntryCount == 0)
        {
            SpanBufferWriter emptyWriter = new(output);
            using HsstBuilder<SpanBufferWriter> empty = new(ref emptyWriter);
            empty.Build();
            return emptyWriter.Written;
        }

        var lookup = destructedAddresses.GetAlternateLookup<ReadOnlySpan<byte>>();

        SpanBufferWriter writer = new(output);
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer, outerMinSep);
        using Hsst.Hsst.Enumerator olderEnum = olderHsst.GetEnumerator();
        using Hsst.Hsst.Enumerator newerEnum = newerHsst.GetEnumerator();

        bool hasOlder = olderEnum.MoveNext();
        bool hasNewer = newerEnum.MoveNext();

        while (hasOlder && hasNewer)
        {
            ReadOnlySpan<byte> olderKey = olderEnum.Current.Key;
            ReadOnlySpan<byte> newerKey = newerEnum.Current.Key;

            int cmp = olderKey.SequenceCompareTo(newerKey);
            if (cmp < 0)
            {
                // Only in older: skip if destructed
                if (!lookup.Contains(olderKey))
                    builder.Add(olderKey, olderEnum.Current.Value);
                hasOlder = olderEnum.MoveNext();
            }
            else if (cmp > 0)
            {
                builder.Add(newerKey, newerEnum.Current.Value);
                hasNewer = newerEnum.MoveNext();
            }
            else
            {
                if (lookup.Contains(newerKey))
                {
                    // Destructed: use newer only, don't merge inner HSSTs
                    builder.Add(newerKey, newerEnum.Current.Value);
                }
                else
                {
                    // Not destructed: merge prefix-level HSSTs (each prefix maps to a suffix HSST)
                    ref SpanBufferWriter innerWriter = ref builder.BeginValueWrite();
                    int mergedLen = NestedStreamingMerge(
                        olderEnum.Current.Value, newerEnum.Current.Value, innerWriter.GetSpan(0),
                        innerMinSep, innerMinSep, innerInline);
                    innerWriter.Advance(mergedLen);
                    builder.FinishValueWrite(newerKey);
                }
                hasOlder = olderEnum.MoveNext();
                hasNewer = newerEnum.MoveNext();
            }
        }

        while (hasOlder)
        {
            if (!lookup.Contains(olderEnum.Current.Key))
                builder.Add(olderEnum.Current.Key, olderEnum.Current.Value);
            hasOlder = olderEnum.MoveNext();
        }

        while (hasNewer)
        {
            builder.Add(newerEnum.Current.Key, newerEnum.Current.Value);
            hasNewer = newerEnum.MoveNext();
        }

        builder.Build();
        return writer.Written;
    }

    /// <summary>
    /// Merge two address-grouped HSSTs where values are inner HSSTs.
    /// For matching address keys, the inner HSSTs are merged via StreamingMerge.
    /// For non-matching keys, inner HSSTs are copied as-is.
    /// </summary>
    internal static int NestedStreamingMerge(ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output,
        int outerMinSep = 0, int innerMinSep = 0, bool innerInline = false)
    {
        Hsst.Hsst olderHsst = new(older);
        Hsst.Hsst newerHsst = new(newer);

        if (olderHsst.EntryCount == 0 && newerHsst.EntryCount == 0)
        {
            SpanBufferWriter emptyWriter = new(output);
            using HsstBuilder<SpanBufferWriter> empty = new(ref emptyWriter);
            empty.Build();
            return emptyWriter.Written;
        }

        SpanBufferWriter writer = new(output);
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer, outerMinSep);
        using Hsst.Hsst.Enumerator olderEnum = olderHsst.GetEnumerator();
        using Hsst.Hsst.Enumerator newerEnum = newerHsst.GetEnumerator();

        bool hasOlder = olderEnum.MoveNext();
        bool hasNewer = newerEnum.MoveNext();

        while (hasOlder && hasNewer)
        {
            ReadOnlySpan<byte> olderKey = olderEnum.Current.Key;
            ReadOnlySpan<byte> newerKey = newerEnum.Current.Key;

            int cmp = olderKey.SequenceCompareTo(newerKey);
            if (cmp < 0)
            {
                builder.Add(olderKey, olderEnum.Current.Value);
                hasOlder = olderEnum.MoveNext();
            }
            else if (cmp > 0)
            {
                builder.Add(newerKey, newerEnum.Current.Value);
                hasNewer = newerEnum.MoveNext();
            }
            else
            {
                // Matching address key: merge the inner HSSTs directly into output
                ref SpanBufferWriter innerWriter = ref builder.BeginValueWrite();
                int mergedLen = StreamingMerge(
                    olderEnum.Current.Value, newerEnum.Current.Value, innerWriter.GetSpan(0), 0, innerMinSep, innerInline);
                innerWriter.Advance(mergedLen);
                builder.FinishValueWrite(newerKey);
                hasOlder = olderEnum.MoveNext();
                hasNewer = newerEnum.MoveNext();
            }
        }

        while (hasOlder)
        {
            builder.Add(olderEnum.Current.Key, olderEnum.Current.Value);
            hasOlder = olderEnum.MoveNext();
        }

        while (hasNewer)
        {
            builder.Add(newerEnum.Current.Key, newerEnum.Current.Value);
            hasNewer = newerEnum.MoveNext();
        }

        builder.Build();
        return writer.Written;
    }

    /// <summary>
    /// Byte offset from outer span start to inner span start.
    /// Both spans must reference the same underlying memory (inner is a sub-span of outer).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SpanOffset(ReadOnlySpan<byte> outer, ReadOnlySpan<byte> inner) =>
        inner.IsEmpty ? 0 : (int)Unsafe.ByteOffset(
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(outer)),
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(inner)));

    /// <summary>
    /// Like <see cref="StreamingMerge"/> but emits 8-byte <see cref="NodeRef"/> values instead of copying
    /// trie node RLP inline. If <paramref name="olderHasNodeRefs"/> or <paramref name="newerHasNodeRefs"/>
    /// is true, existing values from that side are already NodeRefs and are forwarded as-is.
    /// </summary>
    internal static int StreamingMergeWithNodeRef(
        ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output,
        int olderSnapshotId, ReadOnlySpan<byte> olderFullData,
        int newerSnapshotId, ReadOnlySpan<byte> newerFullData,
        bool olderHasNodeRefs, bool newerHasNodeRefs,
        int startOffset = 0, int minSeparatorLength = 0, bool inlineValues = false)
    {
        Hsst.Hsst olderHsst = new(older);
        Hsst.Hsst newerHsst = new(newer);

        SpanBufferWriter writer = new(output[startOffset..]);
        HsstBuilder<SpanBufferWriter> builder = new(ref writer, minSeparatorLength, inlineValues);

        Hsst.Hsst.Enumerator olderEnum = olderHsst.GetEnumerator();
        Hsst.Hsst.Enumerator newerEnum = newerHsst.GetEnumerator();

        bool hasOlder = olderEnum.MoveNext();
        bool hasNewer = newerEnum.MoveNext();

        int olderColumnOffset = SpanOffset(olderFullData, older);
        int newerColumnOffset = SpanOffset(newerFullData, newer);
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];

        while (hasOlder && hasNewer)
        {
            ReadOnlySpan<byte> olderKey = olderEnum.Current.Key;
            ReadOnlySpan<byte> newerKey = newerEnum.Current.Key;

            int cmp = olderKey.SequenceCompareTo(newerKey);
            if (cmp < 0)
            {
                AddAsNodeRef(ref builder, olderEnum, olderSnapshotId, olderColumnOffset, olderHasNodeRefs, refBytes);
                hasOlder = olderEnum.MoveNext();
            }
            else if (cmp > 0)
            {
                AddAsNodeRef(ref builder, newerEnum, newerSnapshotId, newerColumnOffset, newerHasNodeRefs, refBytes);
                hasNewer = newerEnum.MoveNext();
            }
            else
            {
                AddAsNodeRef(ref builder, newerEnum, newerSnapshotId, newerColumnOffset, newerHasNodeRefs, refBytes);
                hasOlder = olderEnum.MoveNext();
                hasNewer = newerEnum.MoveNext();
            }
        }

        while (hasOlder)
        {
            AddAsNodeRef(ref builder, olderEnum, olderSnapshotId, olderColumnOffset, olderHasNodeRefs, refBytes);
            hasOlder = olderEnum.MoveNext();
        }

        while (hasNewer)
        {
            AddAsNodeRef(ref builder, newerEnum, newerSnapshotId, newerColumnOffset, newerHasNodeRefs, refBytes);
            hasNewer = newerEnum.MoveNext();
        }

        builder.Build();
        builder.Dispose();
        olderEnum.Dispose();
        newerEnum.Dispose();
        return startOffset + writer.Written;
    }

    /// <summary>
    /// Emit an entry as a NodeRef (or forward existing NodeRef) into the builder.
    /// If <paramref name="hasNodeRefs"/> is true, the value is already a NodeRef and is forwarded as-is.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddAsNodeRef(ref HsstBuilder<SpanBufferWriter> builder,
        in Hsst.Hsst.Enumerator enumerator, int snapshotId, int columnOffset,
        bool hasNodeRefs, Span<byte> refBytes)
    {
        ReadOnlySpan<byte> value = enumerator.Current.Value;
        if (hasNodeRefs)
        {
            builder.Add(enumerator.Current.Key, value);
            return;
        }

        NodeRef.Write(refBytes, new NodeRef(snapshotId, columnOffset + enumerator.CurrentMetadataStart));
        builder.Add(enumerator.Current.Key, refBytes);
    }

    /// <summary>
    /// Convert all values in a single HSST to NodeRefs. Used for non-matching address keys
    /// in nested trie node column merges. If <paramref name="hasNodeRefs"/> is true, values are
    /// already NodeRefs and are forwarded as-is.
    /// </summary>
    private static int ConvertToNodeRefs(
        ReadOnlySpan<byte> innerData, Span<byte> output,
        int snapshotId, ReadOnlySpan<byte> fullData, bool hasNodeRefs,
        int minSeparatorLength = 0, bool inlineValues = false)
    {
        Hsst.Hsst hsst = new(innerData);
        SpanBufferWriter writer = new(output);
        HsstBuilder<SpanBufferWriter> builder = new(ref writer, minSeparatorLength, inlineValues);
        Hsst.Hsst.Enumerator e = hsst.GetEnumerator();

        int dataOffset = SpanOffset(fullData, innerData);
        Span<byte> refBytes = stackalloc byte[NodeRef.Size];

        while (e.MoveNext())
        {
            AddAsNodeRef(ref builder, in e, snapshotId, dataOffset, hasNodeRefs, refBytes);
        }

        builder.Build();
        builder.Dispose();
        e.Dispose();
        return writer.Written;
    }

    /// <summary>
    /// Like <see cref="NestedStreamingMerge"/> but the inner merge uses <see cref="StreamingMergeWithNodeRef"/>
    /// to emit NodeRef values for trie node RLP. Non-matching address keys have their inner HSSTs
    /// converted to NodeRefs via <see cref="ConvertToNodeRefs"/>.
    /// </summary>
    internal static int NestedStreamingMergeWithNodeRef(
        ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output,
        int olderSnapshotId, ReadOnlySpan<byte> olderFullData,
        int newerSnapshotId, ReadOnlySpan<byte> newerFullData,
        bool olderHasNodeRefs, bool newerHasNodeRefs,
        int outerMinSep = 0, int innerMinSep = 0, bool innerInline = false)
    {
        Hsst.Hsst olderHsst = new(older);
        Hsst.Hsst newerHsst = new(newer);

        if (olderHsst.EntryCount == 0 && newerHsst.EntryCount == 0)
        {
            SpanBufferWriter emptyWriter = new(output);
            using HsstBuilder<SpanBufferWriter> empty = new(ref emptyWriter);
            empty.Build();
            return emptyWriter.Written;
        }

        SpanBufferWriter writer = new(output);
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer, outerMinSep);
        using Hsst.Hsst.Enumerator olderEnum = olderHsst.GetEnumerator();
        using Hsst.Hsst.Enumerator newerEnum = newerHsst.GetEnumerator();

        bool hasOlder = olderEnum.MoveNext();
        bool hasNewer = newerEnum.MoveNext();

        while (hasOlder && hasNewer)
        {
            ReadOnlySpan<byte> olderKey = olderEnum.Current.Key;
            ReadOnlySpan<byte> newerKey = newerEnum.Current.Key;

            int cmp = olderKey.SequenceCompareTo(newerKey);
            if (cmp < 0)
            {
                ref SpanBufferWriter innerWriter = ref builder.BeginValueWrite();
                int len = ConvertToNodeRefs(olderEnum.Current.Value, innerWriter.GetSpan(0),
                    olderSnapshotId, olderFullData, olderHasNodeRefs, innerMinSep, innerInline);
                innerWriter.Advance(len);
                builder.FinishValueWrite(olderKey);
                hasOlder = olderEnum.MoveNext();
            }
            else if (cmp > 0)
            {
                ref SpanBufferWriter innerWriter = ref builder.BeginValueWrite();
                int len = ConvertToNodeRefs(newerEnum.Current.Value, innerWriter.GetSpan(0),
                    newerSnapshotId, newerFullData, newerHasNodeRefs, innerMinSep, innerInline);
                innerWriter.Advance(len);
                builder.FinishValueWrite(newerKey);
                hasNewer = newerEnum.MoveNext();
            }
            else
            {
                // Matching address key: merge inner HSSTs with NodeRef emission
                ref SpanBufferWriter innerWriter = ref builder.BeginValueWrite();
                int mergedLen = StreamingMergeWithNodeRef(
                    olderEnum.Current.Value, newerEnum.Current.Value, innerWriter.GetSpan(0),
                    olderSnapshotId, olderFullData, newerSnapshotId, newerFullData,
                    olderHasNodeRefs, newerHasNodeRefs,
                    minSeparatorLength: innerMinSep, inlineValues: innerInline);
                innerWriter.Advance(mergedLen);
                builder.FinishValueWrite(newerKey);
                hasOlder = olderEnum.MoveNext();
                hasNewer = newerEnum.MoveNext();
            }
        }

        while (hasOlder)
        {
            ref SpanBufferWriter innerWriter = ref builder.BeginValueWrite();
            int len = ConvertToNodeRefs(olderEnum.Current.Value, innerWriter.GetSpan(0),
                olderSnapshotId, olderFullData, olderHasNodeRefs, innerMinSep, innerInline);
            innerWriter.Advance(len);
            builder.FinishValueWrite(olderEnum.Current.Key);
            hasOlder = olderEnum.MoveNext();
        }

        while (hasNewer)
        {
            ref SpanBufferWriter innerWriter = ref builder.BeginValueWrite();
            int len = ConvertToNodeRefs(newerEnum.Current.Value, innerWriter.GetSpan(0),
                newerSnapshotId, newerFullData, newerHasNodeRefs, innerMinSep, innerInline);
            innerWriter.Advance(len);
            builder.FinishValueWrite(newerEnum.Current.Key);
            hasNewer = newerEnum.MoveNext();
        }

        builder.Build();
        return writer.Written;
    }

    /// <summary>
    /// Merge metadata columns and inject compacted-snapshot flags:
    ///   "noderefs" → [0x01]
    ///   "ref_ids"  → [id1_le32, id2_le32, ...]
    /// Keys are emitted in sorted order.
    /// </summary>
    private static int MetadataMergeWithCompactedFlags(
        ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output, HashSet<int> refIds)
    {
        Hsst.Hsst olderHsst = new(older);
        Hsst.Hsst newerHsst = new(newer);

        SpanBufferWriter writer = new(output);
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer);
        using Hsst.Hsst.Enumerator olderEnum = olderHsst.GetEnumerator();
        using Hsst.Hsst.Enumerator newerEnum = newerHsst.GetEnumerator();

        bool hasOlder = olderEnum.MoveNext();
        bool hasNewer = newerEnum.MoveNext();

        // Synthetic entries to inject at correct sorted positions
        ReadOnlySpan<byte> nodeRefsKey = "noderefs"u8;
        byte[] nodeRefsValue = [0x01];
        bool nodeRefsEmitted = false;

        ReadOnlySpan<byte> refIdsKey = "ref_ids"u8;
        byte[] refIdsValue = new byte[refIds.Count * 4];
        int idx = 0;
        foreach (int id in refIds)
        {
            BitConverter.TryWriteBytes(refIdsValue.AsSpan(idx * 4, 4), id);
            idx++;
        }
        bool refIdsEmitted = false;

        while (hasOlder && hasNewer)
        {
            ReadOnlySpan<byte> olderKey = olderEnum.Current.Key;
            ReadOnlySpan<byte> newerKey = newerEnum.Current.Key;

            int cmp = olderKey.SequenceCompareTo(newerKey);
            ReadOnlySpan<byte> emitKey;
            ReadOnlySpan<byte> emitValue;
            if (cmp < 0)
            {
                emitKey = olderKey;
                emitValue = olderEnum.Current.Value;
                hasOlder = olderEnum.MoveNext();
            }
            else if (cmp > 0)
            {
                emitKey = newerKey;
                emitValue = newerEnum.Current.Value;
                hasNewer = newerEnum.MoveNext();
            }
            else
            {
                emitKey = newerKey;
                emitValue = newerEnum.Current.Value;
                hasOlder = olderEnum.MoveNext();
                hasNewer = newerEnum.MoveNext();
            }

            // Skip old synthetic entries — we'll re-emit fresh ones
            if (emitKey.SequenceEqual(nodeRefsKey) || emitKey.SequenceEqual(refIdsKey)) continue;

            if (!nodeRefsEmitted && nodeRefsKey.SequenceCompareTo(emitKey) < 0) { builder.Add(nodeRefsKey, nodeRefsValue); nodeRefsEmitted = true; }
            if (!refIdsEmitted && refIdsKey.SequenceCompareTo(emitKey) < 0) { builder.Add(refIdsKey, refIdsValue); refIdsEmitted = true; }
            builder.Add(emitKey, emitValue);
        }

        while (hasOlder)
        {
            ReadOnlySpan<byte> key = olderEnum.Current.Key;
            if (!key.SequenceEqual(nodeRefsKey) && !key.SequenceEqual(refIdsKey))
            {
                if (!nodeRefsEmitted && nodeRefsKey.SequenceCompareTo(key) < 0) { builder.Add(nodeRefsKey, nodeRefsValue); nodeRefsEmitted = true; }
                if (!refIdsEmitted && refIdsKey.SequenceCompareTo(key) < 0) { builder.Add(refIdsKey, refIdsValue); refIdsEmitted = true; }
                builder.Add(key, olderEnum.Current.Value);
            }
            hasOlder = olderEnum.MoveNext();
        }

        while (hasNewer)
        {
            ReadOnlySpan<byte> key = newerEnum.Current.Key;
            if (!key.SequenceEqual(nodeRefsKey) && !key.SequenceEqual(refIdsKey))
            {
                if (!nodeRefsEmitted && nodeRefsKey.SequenceCompareTo(key) < 0) { builder.Add(nodeRefsKey, nodeRefsValue); nodeRefsEmitted = true; }
                if (!refIdsEmitted && refIdsKey.SequenceCompareTo(key) < 0) { builder.Add(refIdsKey, refIdsValue); refIdsEmitted = true; }
                builder.Add(key, newerEnum.Current.Value);
            }
            hasNewer = newerEnum.MoveNext();
        }

        // Emit any remaining synthetic entries
        if (!nodeRefsEmitted) builder.Add(nodeRefsKey, nodeRefsValue);
        if (!refIdsEmitted) builder.Add(refIdsKey, refIdsValue);

        builder.Build();
        return writer.Written;
    }

    [MethodImpl(MethodImplOptions.NoOptimization)]
    internal static int StreamingMerge(ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output, int startOffset = 0, int minSeparatorLength = 0, bool inlineValues = false)
    {
        Hsst.Hsst olderHsst = new(older);
        Hsst.Hsst newerHsst = new(newer);

        SpanBufferWriter writer = new(output[startOffset..]);
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer, minSeparatorLength, inlineValues);

        using Hsst.Hsst.Enumerator olderEnum = olderHsst.GetEnumerator();
        using Hsst.Hsst.Enumerator newerEnum = newerHsst.GetEnumerator();

        bool hasOlder = olderEnum.MoveNext();
        bool hasNewer = newerEnum.MoveNext();

        while (hasOlder && hasNewer)
        {
            ReadOnlySpan<byte> olderKey = olderEnum.Current.Key;
            ReadOnlySpan<byte> newerKey = newerEnum.Current.Key;

            int cmp = olderKey.SequenceCompareTo(newerKey);
            if (cmp < 0)
            {
                builder.Add(olderKey, olderEnum.Current.Value);
                hasOlder = olderEnum.MoveNext();
            }
            else if (cmp > 0)
            {
                builder.Add(newerKey, newerEnum.Current.Value);
                hasNewer = newerEnum.MoveNext();
            }
            else
            {
                builder.Add(newerKey, newerEnum.Current.Value);
                hasOlder = olderEnum.MoveNext();
                hasNewer = newerEnum.MoveNext();
            }
        }

        while (hasOlder)
        {
            builder.Add(olderEnum.Current.Key, olderEnum.Current.Value);
            hasOlder = olderEnum.MoveNext();
        }

        while (hasNewer)
        {
            builder.Add(newerEnum.Current.Key, newerEnum.Current.Value);
            hasNewer = newerEnum.MoveNext();
        }

        builder.Build();
        return startOffset + writer.Written;
    }
}
