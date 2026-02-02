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
    ILogger _logger = logManager.GetClassLogger<FlatTreeSyncStore>();
    private bool _wasFinalized = false;

    /// <summary>
    /// Represents a deletion range with from/to bounds (full 256-bit paths).
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
        if (_wasFinalized) throw new InvalidOperationException("Db was finalized");

        byte[]? existingData;
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            // Check if there's an existing node at this path
            existingData = address is null
                ? reader.TryLoadStateRlp(path, ReadFlags.None)
                : reader.TryLoadStorageRlp(address, path, ReadFlags.None);
        }

        // StateId.Sync bypasses from/to validation, allows writing without state continuity
        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        TrieNode node = new(NodeType.Unknown, data.ToArray());
        node.ResolveNode(NullTrieNodeResolver.Instance, path);

        // Decode existing node if present for optimized deletion
        TrieNode? existingNode = null;
        if (existingData is not null)
        {
            existingNode = new TrieNode(NodeType.Unknown, existingData);
            existingNode.ResolveNode(NullTrieNodeResolver.Instance, path);
        }

        if (address is null)
        {
            if (existingNode is not null)
            {
                RequestStateDeletion(writeBatch, path, node, existingNode);
            }

            writeBatch.SetStateTrieNode(path, node);
            WriteAccountFlatEntriesWithDeletion(writeBatch, path, node);
        }
        else
        {
            if (existingNode is not null)
            {
                RequestStorageDeletion(writeBatch, address, path, node, existingNode);
            }

            writeBatch.SetStorageTrieNode(address, path, node);
            WriteStorageFlatEntriesWithDeletion(writeBatch, address, path, node);
        }
    }

    private void WriteAccountFlatEntriesWithDeletion(
        IPersistence.IWriteBatch writeBatch,
        in TreePath path,
        TrieNode node)
    {
        if (node.IsLeaf)
        {
            ValueHash256 fullPath = path.Append(node.Key).Path;
            Account account = AccountDecoder.Instance.Decode(node.Value.Span)!;
            writeBatch.SetAccountRaw(fullPath.ToCommitment(), account);
            return;
        }

        TreePath mutablePath = path;

        if (node.IsBranch)
        {
            FlatEntryWriter.BranchInlineChildLeafEnumerator enumerator = new(ref mutablePath, node);
            while (enumerator.MoveNext())
            {
                ProcessInlineAccountLeaf(writeBatch, enumerator.IntermediatePath, enumerator.CurrentNode);
            }
        }
        else if (node.IsExtension)
        {
            FlatEntryWriter.ExtensionInlineChildLeafEnumerator enumerator = new(ref mutablePath, node);
            while (enumerator.MoveNext())
            {
                ProcessInlineAccountLeaf(writeBatch, enumerator.IntermediatePath, enumerator.CurrentNode);
            }
        }
    }

    private void ProcessInlineAccountLeaf(
        IPersistence.IWriteBatch writeBatch,
        in TreePath childPath,
        TrieNode childNode)
    {
        // Check for existing node at this inline leaf's path
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        byte[]? existingData = reader.TryLoadStateRlp(childPath, ReadFlags.None);

        if (existingData is not null)
        {
            TrieNode existingChildNode = new(NodeType.Unknown, existingData);
            existingChildNode.ResolveNode(NullTrieNodeResolver.Instance, childPath);
            RequestStateDeletion(writeBatch, childPath, childNode, existingChildNode);
        }

        // Write the flat entry
        ValueHash256 fullPath = childPath.Append(childNode.Key).Path;
        Account account = AccountDecoder.Instance.Decode(childNode.Value.Span)!;
        writeBatch.SetAccountRaw(fullPath.ToCommitment(), account);
    }

    private void WriteStorageFlatEntriesWithDeletion(
        IPersistence.IWriteBatch writeBatch,
        Hash256 address,
        in TreePath path,
        TrieNode node)
    {
        if (node.IsLeaf)
        {
            WriteStorageLeafEntry(writeBatch, address, path, node);
            return;
        }

        TreePath mutablePath = path;

        if (node.IsBranch)
        {
            FlatEntryWriter.BranchInlineChildLeafEnumerator enumerator = new(ref mutablePath, node);
            while (enumerator.MoveNext())
            {
                ProcessInlineStorageLeaf(writeBatch, address, enumerator.IntermediatePath, enumerator.CurrentNode);
            }
        }
        else if (node.IsExtension)
        {
            FlatEntryWriter.ExtensionInlineChildLeafEnumerator enumerator = new(ref mutablePath, node);
            while (enumerator.MoveNext())
            {
                ProcessInlineStorageLeaf(writeBatch, address, enumerator.IntermediatePath, enumerator.CurrentNode);
            }
        }
    }

    private void ProcessInlineStorageLeaf(
        IPersistence.IWriteBatch writeBatch,
        Hash256 address,
        in TreePath childPath,
        TrieNode childNode)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        byte[]? existingData = reader.TryLoadStorageRlp(address, childPath, ReadFlags.None);

        if (existingData is not null)
        {
            TrieNode existingChildNode = new(NodeType.Unknown, existingData);
            existingChildNode.ResolveNode(NullTrieNodeResolver.Instance, childPath);
            RequestStorageDeletion(writeBatch, address, childPath, childNode, existingChildNode);
        }

        WriteStorageLeafEntry(writeBatch, address, childPath, childNode);
    }

    private static void WriteStorageLeafEntry(
        IPersistence.IWriteBatch writeBatch,
        Hash256 address,
        in TreePath path,
        TrieNode node)
    {
        ValueHash256 fullPath = path.Append(node.Key).Path;
        ReadOnlySpan<byte> value = node.Value.Span;
        byte[] toWrite = value.IsEmpty
            ? StorageTree.ZeroBytes
            : value.AsRlpValueContext().DecodeByteArray();
        writeBatch.SetStorageRaw(address, fullPath.ToCommitment(), SlotValue.FromSpanWithoutLeadingZero(toWrite));
    }

    private void RequestStateDeletion(IPersistence.IWriteBatch writeBatch, in TreePath path, TrieNode newNode, TrieNode existingNode)
    {
        bool warned = false;
        RefList16<DeletionRange> ranges = new();
        ComputeDeletionRanges(path, newNode, existingNode, ref ranges);
        foreach (DeletionRange range in ranges.AsSpan())
        {
            if (!warned)
            {
                warned = true;
                _logger.Warn($"Deleting path {path}. New node is a {newNode.NodeType}, existing is a {existingNode.NodeType}");
            }
            _logger.Warn($"Deleting path {path}. Range {range.From} to {range.To}");
            writeBatch.DeleteAccountRange(range.From, range.To);
            writeBatch.DeleteStateTrieNodeRange(ComputeTreePathForHash(range.From, 64), ComputeTreePathForHash(range.To, 64));
        }
    }

    private void RequestStorageDeletion(IPersistence.IWriteBatch writeBatch, Hash256 address, in TreePath path, TrieNode newNode, TrieNode existingNode)
    {
        bool warned = false;
        ValueHash256 addressHash = address.ValueHash256;
        RefList16<DeletionRange> ranges = new();
        ComputeDeletionRanges(path, newNode, existingNode, ref ranges);
        foreach (DeletionRange range in ranges.AsSpan())
        {
            if (!warned)
            {
                warned = true;
                _logger.Warn($"Deleting path {address}:{path}. New node is a {newNode.NodeType}, existing is a {existingNode.NodeType}");
            }
            _logger.Warn($"Deleting path {address}:{path}. Range {range.From} to {range.To}");
            writeBatch.DeleteStorageRange(addressHash, range.From, range.To);
            writeBatch.DeleteStorageTrieNodeRange(addressHash, ComputeTreePathForHash(range.From, 64), ComputeTreePathForHash(range.To, 64));
        }
    }

    private static readonly byte[] SingleNibble = [0];
    private static readonly byte[] DummyValue = [0x01];

    /// <summary>
    /// Computes the deletion ranges for a new node, assuming existing node covers the full subtree.
    /// Used for testing and backward compatibility.
    /// </summary>
    internal static void ComputeDeletionRanges(in TreePath path, TrieNode newNode, ref RefList16<DeletionRange> ranges)
    {
        // Create a synthetic "full coverage" existing node (branch with all children non-null)
        TrieNode fullCoverageNode = TrieNodeFactory.CreateBranch();
        for (int i = 0; i < 16; i++)
            fullCoverageNode[i] = TrieNodeFactory.CreateLeaf(SingleNibble, DummyValue);
        ComputeDeletionRanges(path, newNode, fullCoverageNode, ref ranges);
    }

    /// <summary>
    /// Computes the deletion ranges when replacing an existing node with a new node.
    /// Only generates ranges for areas where existing had data but new doesn't.
    /// </summary>
    internal static void ComputeDeletionRanges(in TreePath path, TrieNode newNode, TrieNode existingNode, ref RefList16<DeletionRange> ranges)
    {
        // Optimization: Both nodes have the same key prefix - no deletion needed for matching subtrees
        if (newNode.NodeType == existingNode.NodeType)
        {
            if (newNode.NodeType == NodeType.Branch)
            {
                ComputeBranchToBranchDeletionRanges(path, newNode, existingNode, ref ranges);
                return;
            }

            // Leaf/Extension with same key: existing data is within new coverage, skip deletion
            if ((newNode.NodeType == NodeType.Leaf || newNode.NodeType == NodeType.Extension) &&
                newNode.Key.SequenceEqual(existingNode.Key))
            {
                return;
            }
        }

        // Cross-type transitions or same-type with different keys
        switch (newNode.NodeType)
        {
            case NodeType.Branch:
                ComputeToBranchDeletionRanges(path, newNode, existingNode, ref ranges);
                break;
            case NodeType.Leaf:
                ComputeToLeafDeletionRanges(path, newNode, existingNode, ref ranges);
                break;
            case NodeType.Extension:
                ComputeToExtensionDeletionRanges(path, newNode, existingNode, ref ranges);
                break;
        }
    }

    /// <summary>
    /// Branch to Branch: Only delete children that went from non-null to null.
    /// </summary>
    private static void ComputeBranchToBranchDeletionRanges(TreePath path, TrieNode newNode, TrieNode existingNode, ref RefList16<DeletionRange> ranges)
    {
        int? nibbleRangeStart = null;

        for (int i = 0; i < 16; i++)
        {
            // Only delete if: new has null AND existing had non-null
            bool needsDelete = newNode.IsChildNull(i) && !existingNode.IsChildNull(i);

            if (needsDelete)
            {
                nibbleRangeStart ??= i;
            }
            else if (nibbleRangeStart.HasValue)
            {
                AddMergedRange(ComputeSubtreeRangeForNibble(path, nibbleRangeStart.Value, i - 1), ref ranges);
                nibbleRangeStart = null;
            }
        }

        if (nibbleRangeStart.HasValue)
        {
            AddMergedRange(ComputeSubtreeRangeForNibble(path, nibbleRangeStart.Value, 15), ref ranges);
        }
    }

    /// <summary>
    /// Any node type to Branch: Delete existing coverage that falls in new null children.
    /// </summary>
    private static void ComputeToBranchDeletionRanges(TreePath path, TrieNode newNode, TrieNode existingNode, ref RefList16<DeletionRange> ranges)
    {
        DeletionRange existingCoverage = ComputeExistingNodeCoverage(path, existingNode);

        int? nibbleRangeStart = null;

        void CompleteCurrentRange(int nibble, ref RefList16<DeletionRange> ranges)
        {
            if (nibbleRangeStart.HasValue)
            {
                AddMergedRangeIntersected(ComputeSubtreeRangeForNibble(path, nibbleRangeStart.Value, nibble - 1), existingCoverage, ref ranges);
                nibbleRangeStart = null;
            }
        }

        for (int i = 0; i < 16; i++)
        {
            if (!newNode.IsChildNull(i))
            {
                CompleteCurrentRange(i, ref ranges);
                continue;
            }

            // New has null at position i - check if existing covered this area
            DeletionRange childRange = ComputeSubtreeRange(path.Append(i));
            bool shouldDeleteNibble = RangesOverlap(childRange, existingCoverage);
            if (shouldDeleteNibble)
            {
                nibbleRangeStart ??= i;
            }
            else
            {
                CompleteCurrentRange(i, ref ranges);
            }
        }

        CompleteCurrentRange(16, ref ranges);
    }

    /// <summary>
    /// Any node type to Leaf: Delete existing coverage outside the new leaf's exact path.
    /// </summary>
    private static void ComputeToLeafDeletionRanges(TreePath path, TrieNode newNode, TrieNode existingNode, ref RefList16<DeletionRange> ranges)
    {
        TreePath newFullPath = path.Append(newNode.Key);
        DeletionRange existingCoverage = ComputeExistingNodeCoverage(path, existingNode);

        // Gap before the leaf - only if existing covered it
        ValueHash256 subtreeStart = path.ToLowerBoundPath();
        if (newFullPath.Path.CompareTo(subtreeStart) > 0)
        {
            DeletionRange gapBefore = new(subtreeStart, DecrementPath(newFullPath.Path));
            if (RangesOverlap(gapBefore, existingCoverage))
            {
                DeletionRange clipped = IntersectRanges(gapBefore, existingCoverage);
                if (clipped.To.CompareTo(clipped.From) >= 0)
                    ranges.Add(clipped);
            }
        }

        // Gap after the leaf - only if existing covered it
        ValueHash256 afterLeaf = IncrementPath(newFullPath.Path);
        ValueHash256 subtreeEnd = path.ToUpperBoundPath();
        if (afterLeaf.CompareTo(subtreeEnd) <= 0)
        {
            DeletionRange gapAfter = new(afterLeaf, subtreeEnd);
            if (RangesOverlap(gapAfter, existingCoverage))
            {
                DeletionRange clipped = IntersectRanges(gapAfter, existingCoverage);
                if (clipped.To.CompareTo(clipped.From) >= 0)
                    ranges.Add(clipped);
            }
        }
    }

    /// <summary>
    /// Any node type to Extension: Delete existing coverage outside the new extension's subtree.
    /// </summary>
    private static void ComputeToExtensionDeletionRanges(TreePath path, TrieNode newNode, TrieNode existingNode, ref RefList16<DeletionRange> ranges)
    {
        TreePath extendedPath = path.Append(newNode.Key);
        DeletionRange existingCoverage = ComputeExistingNodeCoverage(path, existingNode);

        // Gap before the extension - only if existing covered it
        ValueHash256 subtreeStart = path.ToLowerBoundPath();
        ValueHash256 extensionStart = extendedPath.ToLowerBoundPath();
        if (extensionStart.CompareTo(subtreeStart) > 0)
        {
            DeletionRange gapBefore = new(subtreeStart, DecrementPath(extensionStart));
            if (RangesOverlap(gapBefore, existingCoverage))
            {
                DeletionRange clipped = IntersectRanges(gapBefore, existingCoverage);
                if (clipped.To.CompareTo(clipped.From) >= 0)
                    ranges.Add(clipped);
            }
        }

        // Gap after the extension - only if existing covered it
        ValueHash256 extensionEnd = extendedPath.ToUpperBoundPath();
        ValueHash256 afterExtension = IncrementPath(extensionEnd);
        ValueHash256 subtreeEnd = path.ToUpperBoundPath();
        if (afterExtension.CompareTo(subtreeEnd) <= 0)
        {
            DeletionRange gapAfter = new(afterExtension, subtreeEnd);
            if (RangesOverlap(gapAfter, existingCoverage))
            {
                DeletionRange clipped = IntersectRanges(gapAfter, existingCoverage);
                if (clipped.To.CompareTo(clipped.From) >= 0)
                    ranges.Add(clipped);
            }
        }
    }

    /// <summary>
    /// Computes the coverage range of an existing node at the given path.
    /// </summary>
    private static DeletionRange ComputeExistingNodeCoverage(in TreePath path, TrieNode existingNode) =>
        existingNode.NodeType switch
        {
            NodeType.Branch => ComputeSubtreeRange(path),
            NodeType.Leaf => new DeletionRange(path.Append(existingNode.Key).Path, path.Append(existingNode.Key).Path),
            NodeType.Extension => ComputeSubtreeRange(path.Append(existingNode.Key)),
            _ => new DeletionRange(ValueKeccak.Zero, ValueKeccak.Zero)
        };

    /// <summary>
    /// Adds a deletion range to the list.
    /// </summary>
    private static void AddMergedRange(DeletionRange range, ref RefList16<DeletionRange> ranges) =>
        ranges.Add(range);

    /// <summary>
    /// Adds a deletion range, but clips it to only the portion that overlaps with the existing node's coverage.
    ///
    /// Example: New branch has null children 3-7, existing was a leaf at "5abc..."
    ///
    ///   New branch null range:     |-------- 3... to 7fff... --------|
    ///   Existing leaf coverage:              |5abc|
    ///   Result (intersection):               |5abc|  (only delete what existed)
    ///
    /// Without intersection, we'd issue unnecessary deletes for empty ranges 3..., 4..., 6..., 7...
    /// </summary>
    private static void AddMergedRangeIntersected(DeletionRange range, DeletionRange existingCoverage, ref RefList16<DeletionRange> ranges)
    {
        if (!RangesOverlap(range, existingCoverage)) return;

        DeletionRange clipped = IntersectRanges(range, existingCoverage);
        if (clipped.To.CompareTo(clipped.From) >= 0)
            ranges.Add(clipped);
    }

    private static bool RangesOverlap(DeletionRange a, DeletionRange b) =>
        a.From.CompareTo(b.To) <= 0 && b.From.CompareTo(a.To) <= 0;

    private static DeletionRange IntersectRanges(DeletionRange a, DeletionRange b) =>
        new(a.From.CompareTo(b.From) > 0 ? a.From : b.From,
            a.To.CompareTo(b.To) < 0 ? a.To : b.To);

    /// <summary>
    /// Compute the range of full paths covered by a subtree rooted at childPath.
    /// </summary>
    private static DeletionRange ComputeSubtreeRange(in TreePath childPath) =>
        new(childPath.ToLowerBoundPath(), childPath.ToUpperBoundPath());

    /// <summary>
    /// Compute the merged range covering path.from.0000... to path.to.ffff... for a nibble range.
    /// </summary>
    private static DeletionRange ComputeSubtreeRangeForNibble(TreePath path, int from, int to) =>
        new(path.Append(from).ToLowerBoundPath(), path.Append(to).ToUpperBoundPath());

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
        if (Interlocked.CompareExchange(ref _wasFinalized, true, false)) throw new InvalidOperationException("Db was finalized");

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
    }

    private class FlatSyncStorageTrieStore(IPersistence.IPersistenceReader reader, Hash256 address) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStorageRlp(address, path, flags);
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
