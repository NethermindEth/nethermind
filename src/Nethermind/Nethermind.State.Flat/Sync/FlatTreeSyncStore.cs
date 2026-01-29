// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync;

public class FlatTreeSyncStore(IPersistence persistence, IPersistenceManager persistenceManager, ILogManager logManager) : ITreeSyncStore
{
    /// <summary>
    /// Represents a deletion range with from/to bounds.
    /// </summary>
    /// <param name="From">Inclusive lower bound of the range.</param>
    /// <param name="To">Inclusive upper bound of the range.</param>
    internal readonly record struct DeletionRange(ValueHash256 From, ValueHash256 To);
    public bool NodeExists(Hash256? address, in TreePath path, in ValueHash256 hash)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        byte[]? data = address is null
            ? reader.TryLoadStateRlp(path, ReadFlags.None)
            : reader.TryLoadStorageRlp(address, path, ReadFlags.None);

        if (data is null) return false;

        // Rehash and verify
        ValueHash256 computedHash = ValueKeccak.Compute(data);
        return computedHash == hash;
    }

    public void SaveNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data)
    {
        byte[]? existingData;
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            // Check if there's an existing node at this path
            existingData = address is null
                ? reader.TryLoadStateRlp(path, ReadFlags.None)
                : reader.TryLoadStorageRlp(address, path, ReadFlags.None);
        }

        // Deletion needed if there's existing data at this path
        bool needsDelete = existingData is not null;

        // StateId.Sync bypasses from/to validation, allows writing without state continuity
        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        TrieNode node = new(NodeType.Unknown, data.ToArray(), isDirty: true);
        node.ResolveNode(NullTrieNodeResolver.Instance, path);

        if (address is null)
        {
            if (needsDelete)
            {
                RequestStateDeletion(writeBatch, path, node);
            }

            writeBatch.SetStateTrieNode(path, node);

            // For leaf nodes, also write the flat account entry
            if (node.IsLeaf)
            {
                ValueHash256 fullPath = path.Append(node.Key).Path;
                Account account = AccountDecoder.Instance.Decode(node.Value.Span)!;
                writeBatch.SetAccountRaw(fullPath.ToCommitment(), account);
            }
        }
        else
        {
            if (needsDelete)
            {
                RequestStorageDeletion(writeBatch, address, path, node);
            }

            writeBatch.SetStorageTrieNode(address, path, node);

            // For leaf nodes, also write the flat storage entry
            if (node.IsLeaf)
            {
                ValueHash256 fullPath = path.Append(node.Key).Path;
                ReadOnlySpan<byte> value = node.Value.Span;
                byte[] toWrite = value.IsEmpty
                    ? StorageTree.ZeroBytes
                    : value.AsRlpValueContext().DecodeByteArray();
                writeBatch.SetStorageRaw(address, fullPath.ToCommitment(), SlotValue.FromSpanWithoutLeadingZero(toWrite));
            }
        }
    }

    private static void RequestStateDeletion(IPersistence.IWriteBatch writeBatch, in TreePath path, TrieNode node)
    {
        RefList16<DeletionRange> ranges = new();
        ComputeDeletionRanges(path, node, ref ranges);
        foreach (DeletionRange range in ranges.AsSpan())
        {
            writeBatch.DeleteAccountRange(range.From, range.To);
            writeBatch.DeleteStateTrieNodeRange(ComputeTreePathForHash(range.From, 64), ComputeTreePathForHash(range.To, 64));
        }
    }

    private static void RequestStorageDeletion(IPersistence.IWriteBatch writeBatch, Hash256 address, in TreePath path, TrieNode node)
    {
        ValueHash256 addressHash = address.ValueHash256;
        RefList16<DeletionRange> ranges = new();
        ComputeDeletionRanges(path, node, ref ranges);
        foreach (DeletionRange range in ranges.AsSpan())
        {
            writeBatch.DeleteStorageRange(addressHash, range.From, range.To);
            writeBatch.DeleteStorageTrieNodeRange(addressHash, ComputeTreePathForHash(range.From, 64), ComputeTreePathForHash(range.To, 64));
        }
    }

    /// <summary>
    /// Computes the deletion ranges for a trie node at a given path.
    /// Populates ranges with ValueHash256 ranges that should be deleted.
    /// </summary>
    internal static void ComputeDeletionRanges(in TreePath path, TrieNode node, ref RefList16<DeletionRange> ranges)
    {
        switch (node.NodeType)
        {
            case NodeType.Branch:
                ComputeBranchDeletionRanges(path, node, ref ranges);
                break;
            case NodeType.Leaf:
                ComputeLeafDeletionRanges(path, node, ref ranges);
                break;
            case NodeType.Extension:
                ComputeExtensionDeletionRanges(path, node, ref ranges);
                break;
        }
    }

    /// <summary>
    /// For each group of consecutive null children in the branch, adds a single range covering their subtrees.
    /// </summary>
    private static void ComputeBranchDeletionRanges(TreePath path, TrieNode node, ref RefList16<DeletionRange> ranges)
    {
        int? rangeStart = null;

        for (int i = 0; i < 16; i++)
        {
            if (node.IsChildNull(i))
            {
                rangeStart ??= i;
            }
            else if (rangeStart.HasValue)
            {
                AddMergedRange(path, rangeStart.Value, i - 1, ref ranges);
                rangeStart = null;
            }
        }

        // Handle trailing null children
        if (rangeStart.HasValue)
        {
            AddMergedRange(path, rangeStart.Value, 15, ref ranges);
        }
    }

    private static void AddMergedRange(TreePath path, int startChild, int endChild, ref RefList16<DeletionRange> ranges)
    {
        TreePath startPath = path.Append(startChild);
        TreePath endPath = path.Append(endChild);
        ValueHash256 from = startPath.Append(0, 64 - startPath.Length).Path;
        ValueHash256 to = endPath.Append(0xF, 64 - endPath.Length).Path;
        ranges.Add(new DeletionRange(from, to));
    }

    /// <summary>
    /// A leaf at this path represents a single entry. Adds ranges for the gaps
    /// before and after the leaf within the current subtree.
    /// </summary>
    private static void ComputeLeafDeletionRanges(TreePath path, TrieNode node, ref RefList16<DeletionRange> ranges)
    {
        TreePath fullPath = path.Append(node.Key);

        // Gap before the leaf
        if (fullPath.Path != path.Append(0, 64 - path.Length).Path)
        {
            ValueHash256 gapFrom = path.Append(0, 64 - path.Length).Path;
            ValueHash256 gapTo = DecrementPath(fullPath.Path);
            if (gapTo.CompareTo(gapFrom) >= 0)
                ranges.Add(new DeletionRange(gapFrom, gapTo));
        }

        // Gap after the leaf
        ValueHash256 afterLeaf = IncrementPath(fullPath.Path);
        ValueHash256 endOfSubtree = path.Append(0xF, 64 - path.Length).Path;
        if (afterLeaf.CompareTo(endOfSubtree) <= 0)
            ranges.Add(new DeletionRange(afterLeaf, endOfSubtree));
    }

    /// <summary>
    /// Extension node: adds ranges for the gaps before and after the extension's subtree.
    /// </summary>
    private static void ComputeExtensionDeletionRanges(TreePath path, TrieNode node, ref RefList16<DeletionRange> ranges)
    {
        TreePath extendedPath = path.Append(node.Key);

        // Gap before the extension's subtree
        if (extendedPath.Path != path.Append(0, 64 - path.Length).Path)
        {
            ValueHash256 gapFrom = path.Append(0, 64 - path.Length).Path;
            ValueHash256 gapTo = DecrementPath(extendedPath.Append(0, 64 - extendedPath.Length).Path);
            if (gapTo.CompareTo(gapFrom) >= 0)
                ranges.Add(new DeletionRange(gapFrom, gapTo));
        }

        // Gap after the extension's subtree
        ValueHash256 afterExtension = IncrementPath(extendedPath.Append(0xF, 64 - extendedPath.Length).Path);
        ValueHash256 endOfSubtree = path.Append(0xF, 64 - path.Length).Path;
        if (afterExtension.CompareTo(endOfSubtree) <= 0)
            ranges.Add(new DeletionRange(afterExtension, endOfSubtree));
    }

    /// <summary>
    /// Compute the range of full paths covered by a subtree rooted at childPath.
    /// </summary>
    private static (ValueHash256 fromPath, ValueHash256 toPath) ComputeSubtreeRange(in TreePath childPath)
    {
        // Lower bound: childPath padded with zeros to 64 nibbles
        ValueHash256 fromPath = childPath.Append(0, 64 - childPath.Length).Path;
        // Upper bound: childPath padded with F's to 64 nibbles
        ValueHash256 toPath = childPath.Append(0xF, 64 - childPath.Length).Path;
        return (fromPath, toPath);
    }

    /// <summary>
    /// Create a TreePath from a ValueHash256 with specified length.
    /// </summary>
    private static TreePath ComputeTreePathForHash(in ValueHash256 hash, int length) =>
        new(hash, length);

    /// <summary>
    /// Decrement a path by 1 (treating it as a 256-bit big-endian integer).
    /// </summary>
    internal static ValueHash256 DecrementPath(in ValueHash256 path)
    {
        ValueHash256 result = path;
        Span<byte> bytes = result.BytesAsSpan;

        for (int i = 31; i >= 0; i--)
        {
            if (bytes[i] > 0)
            {
                bytes[i]--;
                return result;
            }
            bytes[i] = 0xFF;
        }

        // Underflow - return zero (shouldn't happen in practice)
        return ValueKeccak.Zero;
    }

    /// <summary>
    /// Increment a path by 1 (treating it as a 256-bit big-endian integer).
    /// </summary>
    internal static ValueHash256 IncrementPath(in ValueHash256 path)
    {
        ValueHash256 result = path;
        Span<byte> bytes = result.BytesAsSpan;

        for (int i = 31; i >= 0; i--)
        {
            if (bytes[i] < 0xFF)
            {
                bytes[i]++;
                return result;
            }
            bytes[i] = 0x00;
        }

        // Overflow - return max (shouldn't happen in practice)
        result = ValueKeccak.Zero;
        result.BytesAsSpan.Fill(0xFF);
        return result;
    }

    public void FinalizeSync(BlockHeader pivotHeader)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        StateId from = reader.CurrentState;
        StateId to = new StateId(pivotHeader);

        // Create and immediately dispose to increment state ID
        // This pattern is used by Importer - the from->to transition updates the current state pointer
        using (persistence.CreateWriteBatch(from, to))
        {
            // Empty batch - just incrementing state
        }
        persistenceManager.ResetPersistedStateId();

        persistence.Flush();
    }

    public ITreeSyncVerificationContext CreateVerificationContext(byte[] rootNodeData) =>
        new FlatVerificationContext(persistence, rootNodeData, logManager);

    private class FlatVerificationContext : ITreeSyncVerificationContext, IDisposable
    {
        private readonly StateTree _stateTree;
        private readonly IPersistence.IPersistenceReader _reader;
        private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;

        public FlatVerificationContext(IPersistence persistence, byte[] rootNodeData, ILogManager logManager)
        {
            _reader = persistence.CreateReader();
            _stateTree = new StateTree(new FlatSyncTrieStore(_reader), logManager);
            _stateTree.RootRef = new TrieNode(NodeType.Unknown, rootNodeData);
        }

        public Account? GetAccount(Hash256 addressHash)
        {
            ReadOnlySpan<byte> bytes = _stateTree.Get(addressHash.Bytes);
            return bytes.IsEmpty ? null : _accountDecoder.Decode(bytes);
        }

        public void Dispose() => _reader.Dispose();
    }

    /// <summary>
    /// Minimal trie store for verification context using IPersistenceReader directly.
    /// </summary>
    private class FlatSyncTrieStore(IPersistence.IPersistenceReader reader) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStateRlp(path, flags);

        public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            address is null ? this : new FlatSyncStorageTrieStore(reader, address);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            throw new NotSupportedException("Read-only");
    }

    private class FlatSyncStorageTrieStore(IPersistence.IPersistenceReader reader, Hash256 address) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStorageRlp(address, path, flags);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            throw new NotSupportedException("Read-only");
    }

    /// <summary>
    /// Minimal trie node resolver that throws on all operations.
    /// Used only for ResolveNode where the node already has RLP data, so the resolver is never actually called.
    /// </summary>
    private sealed class NullTrieNodeResolver : ITrieNodeResolver
    {
        public static readonly NullTrieNodeResolver Instance = new();
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => throw new NotSupportedException();
        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => throw new NotSupportedException();
        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => throw new NotSupportedException();
        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => throw new NotSupportedException();
        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;
    }
}
