// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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

    internal readonly record struct DeletionRange(ValueHash256 From, ValueHash256 To);

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
        // Flat account entries: value ranges (accounts have no depth; merged ranges are the efficient form).
        RefList16<DeletionRange> ranges = new();
        ComputeDeletionRanges(path, newNode, existingNode, ref ranges);
        foreach (DeletionRange range in ranges.AsSpan())
            writeBatch.DeleteAccountRange(range.From, range.To);

        // Trie nodes: one subtree per region, so deletion is floored at each subtree's depth (see DeleteStateSubTree).
        StateSubtreeDeleter deleter = new(writeBatch);
        ComputeDeletionSubtrees(path, newNode, existingNode, ref deleter);
    }

    private void RequestStorageDeletion(IPersistence.IWriteBatch writeBatch, Hash256 address, in TreePath path, TrieNode newNode, TrieNode? existingNode)
    {
        ValueHash256 addressHash = address.ValueHash256;

        // Flat storage entries: value ranges (slots have no depth; merged ranges are the efficient form).
        RefList16<DeletionRange> ranges = new();
        ComputeDeletionRanges(path, newNode, existingNode, ref ranges);
        foreach (DeletionRange range in ranges.AsSpan())
            writeBatch.DeleteStorageRange(addressHash, range.From, range.To);

        // Trie nodes: one subtree per region, so deletion is floored at each subtree's depth (see DeleteStorageSubTree).
        StorageSubtreeDeleter deleter = new(writeBatch, addressHash);
        ComputeDeletionSubtrees(path, newNode, existingNode, ref deleter);
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
        (bool existingIsBranch, int childNibble) = BranchDeletionContext(existingNode);

        int? nibbleRangeStart = null;
        for (int i = 0; i < 16; i++)
        {
            if (ChildNeedsDeletion(newNode, existingNode, existingIsBranch, childNibble, i))
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
    /// To Leaf: If existing is also Leaf with same key, no deletion needed.
    /// Otherwise delete the whole subtree.
    /// </summary>
    private static void ComputeToLeafDeletionRanges(TreePath path, TrieNode newNode, TrieNode? existingNode, ref RefList16<DeletionRange> ranges)
    {
        if (existingNode is not { NodeType: NodeType.Leaf } || !newNode.Key.SequenceEqual(existingNode.Key))
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
    /// Computes the subtree roots to delete when replacing an existing node with a new node — the same regions as
    /// <see cref="ComputeDeletionRanges"/>, but as one <see cref="TreePath"/> root per subtree so trie-node deletion
    /// is depth-exact. Each root is streamed to <paramref name="sink"/> (the extension case can emit more roots than
    /// would fit a fixed-size buffer).
    /// </summary>
    internal static void ComputeDeletionSubtrees<TSink>(in TreePath path, TrieNode newNode, TrieNode? existingNode, ref TSink sink)
        where TSink : ISubtreeSink
    {
        switch (newNode.NodeType)
        {
            case NodeType.Branch:
                (bool existingIsBranch, int childNibble) = BranchDeletionContext(existingNode);
                for (int i = 0; i < 16; i++)
                {
                    if (ChildNeedsDeletion(newNode, existingNode, existingIsBranch, childNibble, i))
                        sink.Add(path.Append(i)); // one subtree per deleted child (depth path.Length + 1)
                }
                break;
            case NodeType.Leaf:
                if (existingNode is not { NodeType: NodeType.Leaf } || !newNode.Key.SequenceEqual(existingNode.Key))
                    sink.Add(path);
                break;
            case NodeType.Extension:
                ComputeToExtensionDeletionSubtrees(path, newNode, existingNode, ref sink);
                break;
        }
    }

    private static void ComputeToExtensionDeletionSubtrees<TSink>(in TreePath path, TrieNode newNode, TrieNode? existingNode, ref TSink sink)
        where TSink : ISubtreeSink
    {
        if (existingNode is { NodeType: NodeType.Extension } && newNode.Key.SequenceEqual(existingNode.Key))
            return;

        // Delete everything under `path` except the new extension's own subtree (path + key): the sibling subtrees at
        // each level of the key. Extensions are rare, so we just loop rather than merge adjacent siblings into ranges.
        byte[] key = newNode.Key!;
        TreePath prefix = path;
        for (int j = 0; j < key.Length; j++)
        {
            int keyNibble = key[j];
            for (int c = 0; c < 16; c++)
            {
                if (c != keyNibble) sink.Add(prefix.Append(c));
            }
            prefix = prefix.Append(keyNibble);
        }
    }

    /// <summary>Receives the subtree roots produced by <see cref="ComputeDeletionSubtrees"/>.</summary>
    internal interface ISubtreeSink
    {
        void Add(in TreePath subtreeRoot);
    }

    private readonly struct StateSubtreeDeleter(IPersistence.IWriteBatch writeBatch) : ISubtreeSink
    {
        public void Add(in TreePath subtreeRoot) => writeBatch.DeleteStateSubTree(subtreeRoot);
    }

    private readonly struct StorageSubtreeDeleter(IPersistence.IWriteBatch writeBatch, ValueHash256 addressHash) : ISubtreeSink
    {
        public void Add(in TreePath subtreeRoot) => writeBatch.DeleteStorageSubTree(addressHash, subtreeRoot);
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
