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
    // For flat, one cannot continue syncing after finalization as it will corrupt existing state.
    private bool _wasFinalized = false;

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

        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        TrieNode node = new(NodeType.Unknown, data.ToArray());
        node.ResolveNode(NullTrieNodeResolver.Instance, path);

        TrieNode? existingNode = ReadExistingNode(address, path);

        if (address is null)
        {
            RequestStateDeletion(writeBatch, path, node, existingNode);

            writeBatch.SetStateTrieNode(path, node);
            FlatEntryWriter.WriteAccountFlatEntries(writeBatch, path, node);
        }
        else
        {
            RequestStorageDeletion(writeBatch, address, path, node, existingNode);

            writeBatch.SetStorageTrieNode(address, path, node);
            FlatEntryWriter.WriteStorageFlatEntries(writeBatch, address, path, node);
        }
    }

    private TrieNode? ReadExistingNode(Hash256? address, TreePath path)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        byte[]? existingData = address is null
            ? reader.TryLoadStateRlp(path, ReadFlags.None)
            : reader.TryLoadStorageRlp(address, path, ReadFlags.None);
        if (existingData is null) return null;

        TrieNode existingNode = new TrieNode(NodeType.Unknown, existingData);
        existingNode.ResolveNode(NullTrieNodeResolver.Instance, path);
        return existingNode;
    }

    private void RequestStateDeletion(IPersistence.IWriteBatch writeBatch, in TreePath path, TrieNode newNode, TrieNode? existingNode)
    {
        RefList16<DeletionRange> ranges = new();
        ComputeDeletionRanges(path, newNode, existingNode, ref ranges);
        foreach (DeletionRange range in ranges.AsSpan())
        {
            writeBatch.DeleteAccountRange(range.From, range.To);
            writeBatch.DeleteStateTrieNodeRange(ComputeTreePathForHash(range.From, 64), ComputeTreePathForHash(range.To, 64));
        }
    }

    private void RequestStorageDeletion(IPersistence.IWriteBatch writeBatch, Hash256 address, in TreePath path, TrieNode newNode, TrieNode? existingNode)
    {
        ValueHash256 addressHash = address.ValueHash256;
        RefList16<DeletionRange> ranges = new();
        ComputeDeletionRanges(path, newNode, existingNode, ref ranges);
        foreach (DeletionRange range in ranges.AsSpan())
        {
            writeBatch.DeleteStorageRange(addressHash, range.From, range.To);
            writeBatch.DeleteStorageTrieNodeRange(addressHash, ComputeTreePathForHash(range.From, 64), ComputeTreePathForHash(range.To, 64));
        }
    }

    /// <summary>
    /// Computes the deletion ranges when replacing an existing node with a new node.
    /// Only generates ranges for areas where existing had data but new doesn't.
    /// When existingNode is null, assumes full coverage and deletes everything outside new node's coverage.
    /// </summary>
    internal static void ComputeDeletionRanges(in TreePath path, TrieNode newNode, TrieNode? existingNode, ref RefList16<DeletionRange> ranges)
    {
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
    /// To Branch: If existing is also Branch, only delete where existing had hash ref but new doesn't.
    /// Otherwise delete all ranges where new has no hash reference.
    /// </summary>
    private static void ComputeToBranchDeletionRanges(TreePath path, TrieNode newNode, TrieNode? existingNode, ref RefList16<DeletionRange> ranges)
    {
        int? nibbleRangeStart = null;
        bool existingIsBranch = existingNode is { NodeType: NodeType.Branch };

        int childNibble = -1;
        if (!existingIsBranch && existingNode is not null)
        {
            childNibble = existingNode.Key![0];
        }

        for (int i = 0; i < 16; i++)
        {
            bool needsDelete = false;
            bool newNodeHasNonInlineChild = newNode.GetChildHashAsValueKeccak(i, out _);
            bool newNodeIsNullOrInline = !newNodeHasNonInlineChild;

            // Note: for inline node, the child hash is null, hence range will be deleted. But the existingNode child may
            // also be inline node, in which case, it still need to be deleted instead of just assuming its empty.
            if (existingIsBranch)
            {
                // Branch→Branch: only delete where existing had hash ref but new doesn't
                bool existingNodeHasChild = !existingNode!.IsChildNull(i);
                if (existingNodeHasChild)
                {
                    needsDelete = newNodeIsNullOrInline;
                }
                else
                {
                    // Nothing to delete
                }
            }
            else
            {
                if (childNibble == -1 || i == childNibble)
                {
                    // Other→Branch: delete all where new has no hash reference
                    needsDelete = newNodeIsNullOrInline;
                }
            }

            if (needsDelete)
            {
                nibbleRangeStart ??= i;
            }
            else if (nibbleRangeStart.HasValue)
            {
                ranges.Add(ComputeSubtreeRangeForNibble(path, nibbleRangeStart.Value, i - 1));
                nibbleRangeStart = null;
            }
        }

        if (nibbleRangeStart.HasValue)
        {
            ranges.Add(ComputeSubtreeRangeForNibble(path, nibbleRangeStart.Value, 15));
        }
    }

    /// <summary>
    /// To Leaf: If existing is also Leaf with same key, no deletion needed.
    /// Otherwise delete the whole subtree.
    /// </summary>
    private static void ComputeToLeafDeletionRanges(TreePath path, TrieNode newNode, TrieNode? existingNode, ref RefList16<DeletionRange> ranges)
    {
        if (existingNode is { NodeType: NodeType.Leaf } && newNode.Key.SequenceEqual(existingNode.Key))
            return;

        ranges.Add(ComputeSubtreeRange(path));
    }

    /// <summary>
    /// To Extension: If existing is also Extension with same key, no deletion needed.
    /// Otherwise delete gaps before and after the extension's subtree.
    /// </summary>
    private static void ComputeToExtensionDeletionRanges(TreePath path, TrieNode newNode, TrieNode? existingNode, ref RefList16<DeletionRange> ranges)
    {
        if (existingNode is { NodeType: NodeType.Extension } && newNode.Key.SequenceEqual(existingNode.Key))
            return;

        TreePath extendedPath = path.Append(newNode.Key);

        // Gap before the extension
        ValueHash256 subtreeStart = path.ToLowerBoundPath();
        ValueHash256 extensionStart = extendedPath.ToLowerBoundPath();
        if (extensionStart.CompareTo(subtreeStart) > 0)
            ranges.Add(new DeletionRange(subtreeStart, extensionStart.DecrementPath()));

        // Gap after the extension
        ValueHash256 extensionEnd = extendedPath.ToUpperBoundPath();
        ValueHash256 afterExtension = extensionEnd.IncrementPath();
        ValueHash256 subtreeEnd = path.ToUpperBoundPath();
        if (afterExtension.CompareTo(subtreeEnd) <= 0)
            ranges.Add(new DeletionRange(afterExtension, subtreeEnd));
    }

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
    }
}
