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
/// The outer HSST has 9 column entries (tags 0x00-0x08), each containing an inner HSST.
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

            // Column 0x01: Accounts
            WriteAccountsColumn(ref outer, snapshot);

            // Column 0x02: Self-destruct
            WriteSelfDestructColumn(ref outer, snapshot);

            // Column 0x03: State nodes (compact, path length 6-15)
            WriteStateNodesColumnCompact(ref outer, snapshot);

            // Column 0x04: Storage slots
            WriteStorageColumn(ref outer, snapshot);

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

    private static void WriteAccountsColumn<TWriter>(ref HsstBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Sort accounts
        List<(AddressAsKey Key, Account? Value)> accounts = new();
        foreach (KeyValuePair<AddressAsKey, Account?> kv in snapshot.Accounts)
        {
            accounts.Add((kv.Key, kv.Value));
        }
        accounts.Sort((a, b) => a.Key.Value.Bytes.SequenceCompareTo(b.Key.Value.Bytes));

        // Begin outer value write for accounts column
        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using (HsstBuilder<TWriter> inner = new(ref innerWriter))
        {
            byte[] rlpBuffer = new byte[256];
            RlpStream rlpStream = new(rlpBuffer);

            foreach ((AddressAsKey key, Account? value) in accounts)
            {
                if (value is null)
                {
                    inner.Add(key.Value.Bytes, ReadOnlySpan<byte>.Empty);
                }
                else
                {
                    int len = AccountDecoder.Slim.GetLength(value);
                    rlpStream.Reset();
                    AccountDecoder.Slim.Encode(rlpStream, value);
                    inner.Add(key.Value.Bytes, rlpBuffer.AsSpan(0, len));
                }
            }

            inner.Build();
            outer.FinishValueWrite(PersistedSnapshot.AccountTag);
        }
    }

    private static void WriteStorageColumn<TWriter>(ref HsstBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Sort storage by (Address, Slot)
        List<((AddressAsKey Addr, UInt256 Slot) Key, SlotValue? Value)> storages = new();
        foreach (KeyValuePair<(AddressAsKey, UInt256), SlotValue?> kv in snapshot.Storages)
        {
            storages.Add((kv.Key, kv.Value));
        }
        storages.Sort((a, b) =>
        {
            int cmp = a.Key.Addr.Value.Bytes.SequenceCompareTo(b.Key.Addr.Value.Bytes);
            if (cmp != 0) return cmp;
            return a.Key.Slot.CompareTo(b.Key.Slot);
        });

        const int slotPrefixLength = 30;
        const int slotSuffixLength = 2;

        // Address-level HSST: Address(20) -> prefix HSST(SlotPrefix(30) -> suffix HSST(SlotSuffix(2) -> SlotValue))
        ref TWriter addressWriter = ref outer.BeginValueWrite();
        using (HsstBuilder<TWriter> addressLevel = new(ref addressWriter))
        {
            byte[] slotKey = new byte[32];
            int i = 0;
            while (i < storages.Count)
            {
                Address currentAddr = storages[i].Key.Addr;

                ref TWriter prefixWriter = ref addressLevel.BeginValueWrite();
                using HsstBuilder<TWriter> prefixLevel = new(ref prefixWriter);

                while (i < storages.Count && storages[i].Key.Addr == currentAddr)
                {
                    storages[i].Key.Slot.ToBigEndian(slotKey.AsSpan());
                    byte[] currentPrefix = slotKey[..slotPrefixLength].ToArray();

                    ref TWriter suffixWriter = ref prefixLevel.BeginValueWrite();
                    using HsstBuilder<TWriter> suffixLevel = new(ref suffixWriter);

                    while (i < storages.Count && storages[i].Key.Addr == currentAddr)
                    {
                        storages[i].Key.Slot.ToBigEndian(slotKey.AsSpan());
                        if (!slotKey.AsSpan(0, slotPrefixLength).SequenceEqual(currentPrefix))
                            break;

                        SlotValue? value = storages[i].Value;
                        if (value.HasValue)
                        {
                            ReadOnlySpan<byte> withoutLeadingZeros = value.Value.AsReadOnlySpan.WithoutLeadingZeros();
                            suffixLevel.Add(slotKey.AsSpan(slotPrefixLength, slotSuffixLength), withoutLeadingZeros);
                        }
                        else
                        {
                            suffixLevel.Add(slotKey.AsSpan(slotPrefixLength, slotSuffixLength), ReadOnlySpan<byte>.Empty);
                        }
                        i++;
                    }

                    suffixLevel.Build();
                    prefixLevel.FinishValueWrite(currentPrefix);
                }

                prefixLevel.Build();
                addressLevel.FinishValueWrite(currentAddr.Bytes);
            }

            addressLevel.Build();
            outer.FinishValueWrite(PersistedSnapshot.StorageTag);
        }
    }

    private static void WriteSelfDestructColumn<TWriter>(ref HsstBuilder<TWriter> outer, Snapshot snapshot) where TWriter : IByteBufferWriter
    {
        // Sort self-destructs
        List<(AddressAsKey Key, bool Value)> selfDestructs = new();
        foreach (KeyValuePair<AddressAsKey, bool> kv in snapshot.SelfDestructedStorageAddresses)
        {
            selfDestructs.Add((kv.Key, kv.Value));
        }
        selfDestructs.Sort((a, b) => a.Key.Value.Bytes.SequenceCompareTo(b.Key.Value.Bytes));

        ref TWriter innerWriter = ref outer.BeginValueWrite();
        using (HsstBuilder<TWriter> inner = new(ref innerWriter))
        {
            ReadOnlySpan<byte> trueValue = new byte[] { 0x01 };
            foreach ((AddressAsKey key, bool value) in selfDestructs)
            {
                inner.Add(key.Value.Bytes, value ? trueValue : ReadOnlySpan<byte>.Empty);
            }

            inner.Build();
            outer.FinishValueWrite(PersistedSnapshot.SelfDestructTag);
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
        using (HsstBuilder<TWriter> inner = new(ref innerWriter))
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
        using (HsstBuilder<TWriter> inner = new(ref innerWriter))
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
        using (HsstBuilder<TWriter> hashLevel = new(ref hashWriter))
        {
            byte[] pathKey = new byte[8];
            int i = 0;
            while (i < storageNodes.Count)
            {
                Hash256 currentHash = storageNodes[i].Key.Addr;

                ref TWriter innerWriter = ref hashLevel.BeginValueWrite();
                using HsstBuilder<TWriter> inner = new(ref innerWriter);

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
        using (HsstBuilder<TWriter> hashLevel = new(ref hashWriter))
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
        snapshots[0].GetSpan().CopyTo(bufferA);
        int currentLen = snapshots[0].Size;
        int olderSnapshotId = snapshots[0].Id;
        bool olderHasNodeRefs = snapshots[0].Type == PersistedSnapshotType.Compacted;
        resultInA = true;

        for (int i = 1; i < snapshots.Count; i++)
        {
            bool newerHasNodeRefs = snapshots[i].Type == PersistedSnapshotType.Compacted;
            ReadOnlySpan<byte> src = resultInA ? bufferA[..currentLen] : bufferB[..currentLen];
            Span<byte> dst = resultInA ? bufferB : bufferA;
            currentLen = MergeTwoPersisted(src, snapshots[i].GetSpan(), dst,
                olderSnapshotId, snapshots[i].Id,
                olderHasNodeRefs, newerHasNodeRefs,
                referencedIds);
            // After first merge, output always has NodeRefs
            olderHasNodeRefs = true;
            olderSnapshotId = 0;
            resultInA = !resultInA;
        }

        return currentLen;
    }

    /// <summary>
    /// Merge two columnar HSST snapshots with self-destruct awareness.
    ///   - SelfDestruct column: TryAdd semantics (newer=empty->empty, newer=0x01->older if exists)
    ///   - Storage column: destructed addresses' older storage is discarded
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

        // Pre-extract destructed addresses from newer self-destruct column
        bool hasSdTag = newerOuter.TryGet(PersistedSnapshot.SelfDestructTag, out ReadOnlySpan<byte> newerSd);
        Debug.Assert(hasSdTag, $"Missing required tag 0x{PersistedSnapshot.SelfDestructTag[0]:X2} in persisted snapshot");
        HashSet<byte[]> destructedAddresses = new(Bytes.EqualityComparer);
        Hsst.Hsst sdHsst = new(newerSd);
        using Hsst.Hsst.Enumerator sdEnum = sdHsst.GetEnumerator();
        while (sdEnum.MoveNext())
        {
            if (sdEnum.Current.Value.IsEmpty) // destructed
                destructedAddresses.Add(sdEnum.Current.Key.ToArray());
        }

        SpanBufferWriter outerWriter = new(output);
        using HsstBuilder<SpanBufferWriter> outerBuilder = new(ref outerWriter);
        byte[][] tags = [
            PersistedSnapshot.MetadataTag,
            PersistedSnapshot.AccountTag,
            PersistedSnapshot.SelfDestructTag,
            PersistedSnapshot.StateNodeTag,
            PersistedSnapshot.StorageTag,
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
                0x02 => SelfDestructMerge(olderColumn, newerColumn, valueWriter.GetSpan(0)),
                0x04 => NestedStreamingMergeWithSelfDestruct(olderColumn, newerColumn, valueWriter.GetSpan(0), destructedAddresses),
                0x03 or 0x05 or 0x06 => StreamingMergeWithNodeRef(olderColumn, newerColumn, valueWriter.GetSpan(0),
                    olderSnapshotId, olderData, newerSnapshotId, newerData,
                    olderHasNodeRefs, newerHasNodeRefs),
                0x07 or 0x08 => NestedStreamingMergeWithNodeRef(olderColumn, newerColumn, valueWriter.GetSpan(0),
                    olderSnapshotId, olderData, newerSnapshotId, newerData,
                    olderHasNodeRefs, newerHasNodeRefs),
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
    internal static int SelfDestructMerge(ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output)
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
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer);
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
    /// Like <see cref="NestedStreamingMerge"/> but skips older storage for destructed addresses.
    /// When address is destructed:
    ///   - Key in both: use newer only (don't merge inner HSSTs)
    ///   - Key only in older: skip entirely
    ///   - Key only in newer: include (new storage after self-destruct)
    /// </summary>
    internal static int NestedStreamingMergeWithSelfDestruct(
        ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output,
        HashSet<byte[]> destructedAddresses)
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
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer);
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
                        olderEnum.Current.Value, newerEnum.Current.Value, innerWriter.GetSpan(0));
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
    internal static int NestedStreamingMerge(ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output)
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
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer);
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
                    olderEnum.Current.Value, newerEnum.Current.Value, innerWriter.GetSpan(0), 0);
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
        int startOffset = 0)
    {
        Hsst.Hsst olderHsst = new(older);
        Hsst.Hsst newerHsst = new(newer);

        SpanBufferWriter writer = new(output[startOffset..]);
        HsstBuilder<SpanBufferWriter> builder = new(ref writer);

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
        int snapshotId, ReadOnlySpan<byte> fullData, bool hasNodeRefs)
    {
        Hsst.Hsst hsst = new(innerData);
        SpanBufferWriter writer = new(output);
        HsstBuilder<SpanBufferWriter> builder = new(ref writer);
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
        bool olderHasNodeRefs, bool newerHasNodeRefs)
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
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer);
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
                    olderSnapshotId, olderFullData, olderHasNodeRefs);
                innerWriter.Advance(len);
                builder.FinishValueWrite(olderKey);
                hasOlder = olderEnum.MoveNext();
            }
            else if (cmp > 0)
            {
                ref SpanBufferWriter innerWriter = ref builder.BeginValueWrite();
                int len = ConvertToNodeRefs(newerEnum.Current.Value, innerWriter.GetSpan(0),
                    newerSnapshotId, newerFullData, newerHasNodeRefs);
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
                    olderHasNodeRefs, newerHasNodeRefs);
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
                olderSnapshotId, olderFullData, olderHasNodeRefs);
            innerWriter.Advance(len);
            builder.FinishValueWrite(olderEnum.Current.Key);
            hasOlder = olderEnum.MoveNext();
        }

        while (hasNewer)
        {
            ref SpanBufferWriter innerWriter = ref builder.BeginValueWrite();
            int len = ConvertToNodeRefs(newerEnum.Current.Value, innerWriter.GetSpan(0),
                newerSnapshotId, newerFullData, newerHasNodeRefs);
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
    internal static int StreamingMerge(ReadOnlySpan<byte> older, ReadOnlySpan<byte> newer, Span<byte> output, int startOffset = 0, int extraSeparatorLength = 0)
    {
        Hsst.Hsst olderHsst = new(older);
        Hsst.Hsst newerHsst = new(newer);

        SpanBufferWriter writer = new(output[startOffset..]);
        using HsstBuilder<SpanBufferWriter> builder = new(ref writer, extraSeparatorLength);

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
