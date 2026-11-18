// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync;

public class FlatTreeSyncStore(
    IPersistence persistence,
    IPersistenceManager persistenceManager,
    ILogManager logManager,
    IHistoryPivotSeeder? historyPivotSeeder = null) : ITreeSyncStore
{
    // For flat, one cannot continue syncing after finalization as it will corrupt existing state.
    private bool _wasFinalized = false;

    private const int DefaultMaxReplacedNodes = 16_384;

    private readonly Lock _replacedNodesLock = new();
    private readonly Dictionary<(ValueHash256 Address, TreePath Path), ReplacedNode> _replacedNodes = [];

    /// <summary>
    /// The most replaced nodes kept. A node invalidated past this bound is not kept, and saving its path then deletes
    /// everything outside the children of the saved node, as when the node before it is not known.
    /// </summary>
    internal int MaxReplacedNodes { get; init; } = DefaultMaxReplacedNodes;

    internal int ReplacedNodeCount
    {
        get
        {
            lock (_replacedNodesLock)
            {
                return _replacedNodes.Count;
            }
        }
    }

    internal readonly record struct DeletionRange(ValueHash256 From, ValueHash256 To);

    /// <param name="ReceivedHash">The node received for the path and not saved yet.</param>
    /// <param name="PreviousRlp">The node that was stored at the path before, or null when the data below the path is not known.</param>
    private readonly record struct ReplacedNode(ValueHash256 ReceivedHash, byte[]? PreviousRlp);

    public bool NodeExists(Hash256? address, in TreePath path, in ValueHash256 hash)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);

        // Rehash and verify
        ValueHash256 computedHash = default;
        bool found = address is null
            ? reader.TryLoadStateRlp(path, ReadFlags.None, static (data, result) => result[0] = ValueKeccak.Compute(data), new Span<ValueHash256>(ref computedHash))
            : reader.TryLoadStorageRlp(address, path, ReadFlags.None, static (data, result) => result[0] = ValueKeccak.Compute(data), new Span<ValueHash256>(ref computedHash));
        return found && computedHash == hash;
    }

    public void SaveNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data)
    {
        if (_wasFinalized) throw new InvalidOperationException("Db was finalized");

        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        TrieNode node = new(NodeType.Unknown, data.ToArray());
        node.ResolveNode(NullTrieNodeResolver.Instance, path);

        bool wasReplaced;
        ReplacedNode replaced;
        lock (_replacedNodesLock)
        {
            wasReplaced = _replacedNodes.Remove((AddressKey(address), path), out replaced);
        }

        TrieNode? existingNode = wasReplaced
            ? replaced.ReceivedHash == hash && replaced.PreviousRlp is not null ? ResolveNode(replaced.PreviousRlp, path) : null
            : ReadExistingNode(reader, address, path);

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

        if (node.IsExtension && !(existingNode is { NodeType: NodeType.Extension } && node.Key.SequenceEqual(existingNode.Key)))
        {
            DeleteNodesInsideKey(writeBatch, address, path, node);
        }
    }

    public void InvalidateNode(Hash256? address, in TreePath path, in ValueHash256 hash, TrieNode node)
    {
        if (_wasFinalized) throw new InvalidOperationException("Db was finalized");

        (ValueHash256 Address, TreePath Path) key = (AddressKey(address), path);
        byte[]? storedRlp;
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync))
        {
            storedRlp = ReadExistingRlp(reader, address, path);
        }

        if (storedRlp is not null && ValueKeccak.Compute(storedRlp) == hash) return;

        lock (_replacedNodesLock)
        {
            if (storedRlp is not null)
            {
                if (_replacedNodes.Count < MaxReplacedNodes || _replacedNodes.ContainsKey(key))
                {
                    _replacedNodes[key] = new ReplacedNode(hash, storedRlp);
                }
            }
            else if (_replacedNodes.ContainsKey(key))
            {
                _replacedNodes[key] = new ReplacedNode(hash, null);
            }
        }

        if (storedRlp is null && !node.IsExtension && !node.IsLeaf) return;

        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        if (node.IsLeaf && (storedRlp is null || !IsLeafWithKey(ResolveNode(storedRlp, path), node.Key!)))
        {
            DeleteTrieNodesBelow(writeBatch, address, path);
        }
        else if (storedRlp is not null)
        {
            DeleteTrieNode(writeBatch, address, path);
        }
        if (node.IsExtension) DeleteNodesInsideKey(writeBatch, address, path, node);
    }

    private static ValueHash256 AddressKey(Hash256? address) => address?.ValueHash256 ?? default;

    private static TrieNode ResolveNode(byte[] rlp, in TreePath path)
    {
        TrieNode node = new(NodeType.Unknown, rlp);
        node.ResolveNode(NullTrieNodeResolver.Instance, path);
        return node;
    }

    private static void DeleteTrieNode(IPersistence.IWriteBatch writeBatch, Hash256? address, in TreePath path)
    {
        if (address is null)
            writeBatch.DeleteStateTrieNode(path);
        else
            writeBatch.DeleteStorageTrieNode(address, path);
    }

    private static bool IsLeafWithKey(TrieNode node, byte[] key) => node.IsLeaf && node.Key.AsSpan().SequenceEqual(key);

    /// <summary>Deletes the trie node at <paramref name="path"/> and every trie node below it.</summary>
    private static void DeleteTrieNodesBelow(IPersistence.IWriteBatch writeBatch, Hash256? address, in TreePath path)
    {
        DeletionRange range = ComputeSubtreeRange(path);
        if (address is null)
            writeBatch.DeleteStateTrieNodeRange(range.From, range.To);
        else
            writeBatch.DeleteStorageTrieNodeRange(address.ValueHash256, range.From, range.To);
    }

    private static void DeleteNodesInsideKey(IPersistence.IWriteBatch writeBatch, Hash256? address, in TreePath path, TrieNode extension)
    {
        TreePath insidePath = path;
        for (int i = 0; i < extension.Key!.Length - 1; i++)
        {
            insidePath = insidePath.Append(extension.Key[i]);
            DeleteTrieNode(writeBatch, address, insidePath);
        }
    }

    private static TrieNode? ReadExistingNode(IPersistence.IPersistenceReader reader, Hash256? address, TreePath path) =>
        ReadExistingRlp(reader, address, path) is { } existingData ? ResolveNode(existingData, path) : null;

    private static byte[]? ReadExistingRlp(IPersistence.IPersistenceReader reader, Hash256? address, in TreePath path)
    {
        byte[]? existingData = null;
        _ = address is null
            ? reader.TryLoadStateRlp(path, ReadFlags.None, static (value, result) => result[0] = value.ToArray(), new Span<byte[]?>(ref existingData))
            : reader.TryLoadStorageRlp(address, path, ReadFlags.None, static (value, result) => result[0] = value.ToArray(), new Span<byte[]?>(ref existingData));
        return existingData;
    }

    private void RequestStateDeletion(IPersistence.IWriteBatch writeBatch, in TreePath path, TrieNode newNode, TrieNode? existingNode)
    {
        RefList16<DeletionRange> ranges = new();
        ComputeDeletionRanges(path, newNode, existingNode, ref ranges);
        if (ranges.Count == 0) return;

        ValueHash256? keptAccount = newNode.IsLeaf ? path.Append(newNode.Key).ToLowerBoundPath() : null;
        foreach (DeletionRange range in ranges.AsSpan())
        {
            writeBatch.SelfDestructRange(range.From, range.To, keptAccount);
            writeBatch.DeleteAccountRange(range.From, range.To);
            writeBatch.DeleteStateTrieNodeRange(range.From, range.To);
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
            writeBatch.DeleteStorageTrieNodeRange(addressHash, range.From, range.To);
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
                needsDelete = existingNodeHasChild && newNodeIsNullOrInline;
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

    public void EnsureStorageEmpty(in ValueHash256 address)
    {
        // Storage trie nodes go too: NodeExists is keyed by path, so a later round would take a stale node as a complete subtree.
        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        writeBatch.DeleteStorageRange(address, ValueKeccak.Zero, ValueKeccak.MaxValue);
        writeBatch.DeleteStorageTrieNodeRange(address, ValueKeccak.Zero, ValueKeccak.MaxValue);
    }

    public void FinalizeSync(BlockHeader pivotHeader)
    {
        if (Interlocked.CompareExchange(ref _wasFinalized, true, false)) throw new InvalidOperationException("Db was finalized");
        lock (_replacedNodesLock)
        {
            _replacedNodes.Clear();
        }

        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        StateId from = reader.CurrentState;
        StateId to = new(pivotHeader);

        // Snap/heal writes use DisableWAL and are only crash-durable once flushed. Flush before advancing the
        // WAL-durable pointer, so a crash can't leave CurrentState pointing past unflushed (holed) data. #11457
        persistence.Flush();

        // Before the state pointer advances: the reader must see exactly the pivot's state, no block processing
        // yet on top of it. A no-op when history capture or windowing is not configured.
        if (historyPivotSeeder is not null)
        {
            Hash256 pivotStateRoot = pivotHeader.StateRoot ?? throw new InvalidOperationException(
                $"Sync pivot header {pivotHeader.Number} has no state root; cannot seed the flat history floor at it.");
            historyPivotSeeder.SeedPivot(pivotHeader.Number, pivotStateRoot);
        }

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

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            byte[]? rlp = null;
            reader.TryLoadStateRlp(path, flags, static (value, result) => result[0] = value.ToArray(), new Span<byte[]?>(ref rlp));
            return rlp;
        }
    }
}
