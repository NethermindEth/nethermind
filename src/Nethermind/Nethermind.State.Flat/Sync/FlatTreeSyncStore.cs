// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
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

    public bool NodeExists(Hash256? address, in TreePath path, in ValueHash256 hash)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
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

        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        TrieNode node = new(NodeType.Unknown, data.ToArray());
        node.ResolveNode(NullTrieNodeResolver.Instance, path);

        TrieNode? existingNode = ReadExistingNode(reader, address, path);

        if (address is null)
        {
            RequestStateDeletion(writeBatch, path, node, existingNode);

            writeBatch.SetStateTrieNode(path, data);
            FlatEntryWriter.WriteAccountFlatEntries(writeBatch, path, node);
        }
        else
        {
            RequestStorageDeletion(writeBatch, address, path, node, existingNode);

            writeBatch.SetStorageTrieNode(address, path, data);
            FlatEntryWriter.WriteStorageFlatEntries(writeBatch, address, path, node);
        }
    }

    private static TrieNode? ReadExistingNode(IPersistence.IPersistenceReader reader, Hash256? address, TreePath path)
    {
        byte[]? existingData = address is null
            ? reader.TryLoadStateRlp(path, ReadFlags.None)
            : reader.TryLoadStorageRlp(address, path, ReadFlags.None);
        if (existingData is null) return null;

        TrieNode existingNode = new(NodeType.Unknown, existingData);
        existingNode.ResolveNode(NullTrieNodeResolver.Instance, path);
        return existingNode;
    }

    private void RequestStateDeletion(IPersistence.IWriteBatch writeBatch, in TreePath path, TrieNode newNode, TrieNode? existingNode)
    {
        List<TreePath> subtrees = [];
        ComputeDeletionSubtrees(path, newNode, existingNode, subtrees);
        foreach (TreePath subtree in subtrees)
        {
            writeBatch.DeleteAccountRange(subtree.ToLowerBoundPath(), subtree.ToUpperBoundPath());
            writeBatch.DeleteStateSubTree(subtree);
        }
    }

    private void RequestStorageDeletion(IPersistence.IWriteBatch writeBatch, Hash256 address, in TreePath path, TrieNode newNode, TrieNode? existingNode)
    {
        ValueHash256 addressHash = address.ValueHash256;
        List<TreePath> subtrees = [];
        ComputeDeletionSubtrees(path, newNode, existingNode, subtrees);
        foreach (TreePath subtree in subtrees)
        {
            writeBatch.DeleteStorageRange(addressHash, subtree.ToLowerBoundPath(), subtree.ToUpperBoundPath());
            writeBatch.DeleteStorageSubTree(addressHash, subtree);
        }
    }

    private static (bool ExistingIsBranch, int ChildNibble) BranchDeletionContext(TrieNode? existingNode)
    {
        bool existingIsBranch = existingNode is { NodeType: NodeType.Branch };
        int childNibble = !existingIsBranch && existingNode is not null ? existingNode.Key![0] : -1;
        return (existingIsBranch, childNibble);
    }

    private static bool ChildNeedsDeletion(TrieNode newNode, TrieNode? existingNode, bool existingIsBranch, int childNibble, int i)
    {
        // For an inline child the hash is null, so the region is deleted; when the existing child was also inline it
        // still must be deleted rather than assumed empty.
        bool newNodeIsNullOrInline = !newNode.GetChildHashAsValueKeccak(i, out _);
        return existingIsBranch
            // Branch→Branch: only delete where existing had a hash ref but new doesn't
            ? !existingNode!.IsChildNull(i) && newNodeIsNullOrInline
            // Other→Branch: delete all where new has no hash reference
            : (childNibble == -1 || i == childNibble) && newNodeIsNullOrInline;
    }

    /// <summary>
    /// Computes the subtree roots to delete when replacing an existing node with a new node. Only the regions where
    /// existing had data but new doesn't are emitted, as one <see cref="TreePath"/> root per subtree.
    /// </summary>
    internal static void ComputeDeletionSubtrees(in TreePath path, TrieNode newNode, TrieNode? existingNode, List<TreePath> subtrees)
    {
        switch (newNode.NodeType)
        {
            case NodeType.Branch:
                (bool existingIsBranch, int childNibble) = BranchDeletionContext(existingNode);
                for (int i = 0; i < 16; i++)
                {
                    if (ChildNeedsDeletion(newNode, existingNode, existingIsBranch, childNibble, i))
                        subtrees.Add(path.Append(i));
                }
                break;
            case NodeType.Leaf:
                if (existingNode is not { NodeType: NodeType.Leaf } || !newNode.Key.SequenceEqual(existingNode.Key))
                    subtrees.Add(path);
                break;
            case NodeType.Extension:
                ComputeToExtensionDeletionSubtrees(path, newNode, existingNode, subtrees);
                break;
        }
    }

    private static void ComputeToExtensionDeletionSubtrees(in TreePath path, TrieNode newNode, TrieNode? existingNode, List<TreePath> subtrees)
    {
        if (existingNode is { NodeType: NodeType.Extension } && newNode.Key.SequenceEqual(existingNode.Key))
            return;

        // Delete everything under `path` except the new extension's own subtree (path + key): the sibling subtrees at
        // each level of the key. Extensions are rare, so we just loop.
        byte[] key = newNode.Key!;
        TreePath prefix = path;
        for (int j = 0; j < key.Length; j++)
        {
            int keyNibble = key[j];
            for (int c = 0; c < 16; c++)
            {
                if (c != keyNibble) subtrees.Add(prefix.Append(c));
            }
            prefix = prefix.Append(keyNibble);
        }
    }

    public void EnsureStorageEmpty(Hash256 address)
    {
        // Only need to clean flat storage entries. Orphaned storage trie nodes are not a problem
        // because the trie is always traversed from the account's storage root hash — when the
        // account has EmptyTreeHash or the account no longer exists, no storage trie nodes will
        // ever be reached.
        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        ValueHash256 addressHash = address.ValueHash256;
        writeBatch.DeleteStorageRange(addressHash, ValueKeccak.Zero, ValueKeccak.MaxValue);
    }

    public void FinalizeSync(BlockHeader pivotHeader)
    {
        if (Interlocked.CompareExchange(ref _wasFinalized, true, false)) throw new InvalidOperationException("Db was finalized");

        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        StateId from = reader.CurrentState;
        StateId to = new(pivotHeader);

        // Snap/heal writes use DisableWAL and are only crash-durable once flushed. Flush before advancing the
        // WAL-durable pointer, so a crash can't leave CurrentState pointing past unflushed (holed) data. #11457
        persistence.Flush();

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

    private class FlatVerificationContext : ITreeSyncVerificationContext
    {
        private readonly StateTree _stateTree;
        private readonly IPersistence.IPersistenceReader _reader;
        private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;

        public FlatVerificationContext(IPersistence persistence, byte[] rootNodeData, ILogManager logManager)
        {
            _reader = persistence.CreateReader();
            _stateTree = new StateTree(new FlatSyncTrieStore(_reader), logManager)
            {
                RootRef = new TrieNode(NodeType.Unknown, rootNodeData)
            };
        }

        public Account? GetAccount(Hash256 addressHash)
        {
            ReadOnlySpan<byte> bytes = _stateTree.Get(addressHash.Bytes);
            if (bytes.IsEmpty)
            {
                return null;
            }

            RlpReader context = new(bytes);
            return _accountDecoder.Decode(ref context);
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
