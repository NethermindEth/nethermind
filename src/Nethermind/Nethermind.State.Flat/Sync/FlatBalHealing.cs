// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// Flat-backend implementation of <see cref="IBalHealing"/>. Rebuilds a trie's missing internal
/// nodes locally from the disconnected subtrees that snap sync left behind, avoiding the network
/// round-trips of post-snap state healing.
/// </summary>
/// <remarks>
/// Algorithm: from the (potentially missing) root, probe the path-keyed trie node store. If a node exists at
/// the current path it is reused as-is; otherwise the 16 child slots are explored. Each slot is "occupied" iff
/// some flat account/storage leaf with a hash extending through that nibble exists. Recursion descends until a
/// stored trie node is found or a slot proves empty. When a synthesized branch ends up with only one occupied
/// slot it is collapsed: a Branch child becomes a one-nibble Extension; a Leaf/Extension child gets its key
/// prefixed with the slot nibble (avoiding illegal Extension→Extension chains).
///
/// State and storage tries are handled the same way. To bridge the two, the run picks up the
/// accounts whose storage was touched during snap (from <see cref="IStateSyncPivot.UpdatedStorages"/>),
/// reassembles those storage tries first, and rewrites the corresponding state-leaf
/// <see cref="Account.StorageRoot"/> values before re-hashing.
///
/// EIP-7928 (BAL) replay from the snap pivot to head is layered on top of the reassembly: after
/// the trie matches the FIRST pivot snap sync downloaded against, the parent chain is walked from
/// the latest pivot down to the first, and each block's BAL is applied in order to bridge the gap.
/// </remarks>
public sealed class FlatBalHealing(
    IPersistence persistence,
    IStateSyncPivot stateSyncPivot,
    ITreeSyncStore treeSyncStore,
    IWorldStateManager worldStateManager,
    IBlockTree blockTree,
    IBlockAccessListStore balStore,
    ISpecProvider specProvider,
    ILogManager logManager) : IBalHealing
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatBalHealing>();
    private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;
    private readonly ILogManager _logManager = logManager;

    private const int MaxPathLength = 64;
    private const int BranchChildCount = 16;

    /// <inheritdoc/>
    public Task<bool> Run(CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromResult(false);

        BlockHeader? firstPivot = stateSyncPivot.FirstPivot;
        BlockHeader? lastPivot = stateSyncPivot.GetPivotHeader();
        if (firstPivot is null || lastPivot is null)
        {
            if (_logger.IsWarn) _logger.Warn("BAL healing skipped: pivot header not available.");
            return Task.FromResult(false);
        }

        // 1) Reassemble the trie against the FIRST pivot — that's the state snap sync actually wrote.
        Hash256 firstRoot = firstPivot.StateRoot!;
        Hash256[] updatedStorages = stateSyncPivot.UpdatedStorages.ToArray();
        if (_logger.IsInfo) _logger.Info($"Attempting local trie reassembly with {updatedStorages.Length} updated storages, target root {firstRoot} (first pivot {firstPivot.Number}).");

        Hash256? assembledRoot;
        try
        {
            assembledRoot = TryReassemble(updatedStorages);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Trie reassembly failed with exception, falling back to healing: {e}");
            return Task.FromResult(false);
        }

        if (assembledRoot != firstRoot)
        {
            if (_logger.IsWarn) _logger.Warn($"Trie reassembly produced {assembledRoot ?? Keccak.Zero}, expected first pivot {firstRoot}. Falling back to healing.");
            return Task.FromResult(false);
        }

        // 2) Walk the parent chain from last pivot back to first pivot.
        if (!TryBuildBalChain(firstPivot, lastPivot, out List<BlockHeader> chain))
        {
            return Task.FromResult(false);
        }

        Hash256 expectedFinalRoot = lastPivot.StateRoot!;

        if (chain.Count == 0)
        {
            // first == last — snap sync's pivot never advanced. Nothing to replay; the assembled
            // trie already matches the target. Skip BeginScope/BAL apply entirely.
            if (_logger.IsInfo) _logger.Info($"Trie reassembly matches the only pivot {firstRoot}; no BAL replay needed.");
        }
        else
        {
            if (_logger.IsInfo) _logger.Info($"Trie reassembly matches first pivot {firstRoot}; replaying {chain.Count} BAL(s) to reach last pivot {lastPivot.Number}.");

            // 3) Replay BALs forward (firstPivot+1 … lastPivot) against an IWorldState scoped at firstPivot.
            WorldState worldState = new(worldStateManager.GlobalWorldState, _logManager);
            using (worldState.BeginScope(firstPivot))
            {
                foreach (BlockHeader header in chain)
                {
                    if (token.IsCancellationRequested) return Task.FromResult(false);

                    BlockAccessList? bal = balStore.Get(header.Hash!);
                    if (bal is null)
                    {
                        if (_logger.IsWarn) _logger.Warn($"BAL replay aborted: missing BAL for block {header.Number} {header.Hash}.");
                        return Task.FromResult(false);
                    }

                    try
                    {
                        IReleaseSpec spec = specProvider.GetSpec(header);
                        BlockAccessListManager.ApplyStateChanges(bal, worldState, spec, shouldComputeStateRoot: false);
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsWarn) _logger.Warn($"BAL replay threw at block {header.Number} {header.Hash}, falling back to healing: {e}");
                        return Task.FromResult(false);
                    }
                }

                worldState.RecalculateStateRoot();
                if (worldState.StateRoot != expectedFinalRoot)
                {
                    if (_logger.IsWarn) _logger.Warn($"BAL replay produced {worldState.StateRoot}, expected last pivot {expectedFinalRoot}. Falling back to healing.");
                    return Task.FromResult(false);
                }

                worldState.CommitTree(lastPivot.Number);
            }

            if (_logger.IsInfo) _logger.Info($"BAL replay succeeded; state at {lastPivot.Number} matches {expectedFinalRoot}.");
        }

        if (_logger.IsInfo) _logger.Info($"Finalizing sync at last pivot {lastPivot.Number} — skipping traditional healing.");
        treeSyncStore.FinalizeSync(lastPivot);

        // Synchronously walk the just-built trie from the last pivot root to catch any missing/
        // dangling nodes the root-hash chain might have missed. Diagnostic only — sync is already
        // marked complete, but a failure here is a loud signal of internal inconsistency.
        // TODO: drop this once BAL healing has been validated on mainnet.
        if (_logger.IsInfo) _logger.Info("Running post-replay verify-trie pass.");
        bool verified;
        try
        {
            verified = worldStateManager.VerifyTrie(lastPivot, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Post-replay verify-trie threw.", e);
            verified = false;
        }

        if (!verified && _logger.IsError)
        {
            _logger.Error($"POST-REPLAY VERIFY-TRIE FAILED for root {expectedFinalRoot}. Sync was already marked complete; the trie has dangling references.");
        }
        else if (verified && _logger.IsInfo)
        {
            _logger.Info($"Post-replay verify-trie passed for root {expectedFinalRoot}.");
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Walk the parent chain from <paramref name="lastPivot"/> back to <paramref name="firstPivot"/>,
    /// returning the headers in forward order (<c>firstPivot+1 … lastPivot</c>). Returns false (and
    /// an empty list) if the chain doesn't connect, e.g. due to a reorg or a missing header.
    /// </summary>
    private bool TryBuildBalChain(BlockHeader firstPivot, BlockHeader lastPivot, out List<BlockHeader> chain)
    {
        chain = new List<BlockHeader>();

        if (lastPivot.Hash == firstPivot.Hash)
        {
            // Pivot never advanced — nothing to replay.
            return true;
        }

        BlockHeader? cursor = lastPivot;
        while (cursor is not null && cursor.Hash != firstPivot.Hash)
        {
            chain.Add(cursor);
            cursor = blockTree.FindParentHeader(cursor, BlockTreeLookupOptions.None);
        }

        if (cursor?.Hash != firstPivot.Hash)
        {
            if (_logger.IsWarn) _logger.Warn($"BAL healing skipped: parent chain from last pivot {lastPivot.Number} does not reach first pivot {firstPivot.Number}.");
            chain.Clear();
            return false;
        }

        chain.Reverse();
        return true;
    }

    /// <summary>
    /// Reassembles each storage trie listed in <paramref name="updatedStorageAccounts"/> first
    /// (collecting the new <c>StorageRoot</c> per account), then reassembles the state trie
    /// while rewriting state-leaf <c>Account.StorageRoot</c> entries to point at the freshly
    /// assembled storage roots. Returns the reassembled state root, or <see langword="null"/>
    /// if the DB has no leaves to start from.
    /// </summary>
    public Hash256? TryReassemble(IReadOnlyCollection<Hash256> updatedStorageAccounts)
    {
        Dictionary<ValueHash256, Hash256> rewrites = new(updatedStorageAccounts.Count);
        foreach (Hash256 accountHash in updatedStorageAccounts)
        {
            Hash256? newRoot = ReassembleStorageTrie(accountHash.ValueHash256);
            if (newRoot is not null)
            {
                rewrites[accountHash.ValueHash256] = newRoot;
            }
        }

        if (_logger.IsInfo) _logger.Info($"Trie reassembly: rebuilt {rewrites.Count}/{updatedStorageAccounts.Count} storage tries; rebuilding state.");

        return ReassembleStateTrie(rewrites);
    }

    /// <summary>
    /// Reassemble the state trie. If <paramref name="storageRootRewrites"/> is provided, leaves matching an entry
    /// will have their <see cref="Account.StorageRoot"/> rewritten to the corresponding hash.
    /// </summary>
    /// <returns>The root hash of the reassembled trie, or <see langword="null"/> if the DB has no leaves at all.</returns>
    public Hash256? ReassembleStateTrie(IReadOnlyDictionary<ValueHash256, Hash256>? storageRootRewrites = null)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        TreePath path = TreePath.Empty;
        SubtreeResult? result = Reassemble(reader, batch, address: null, ref path, storageRootRewrites);
        return result?.Hash;
    }

    /// <summary>
    /// Reassemble the storage trie of a single account.
    /// </summary>
    /// <returns>The root hash of the reassembled storage trie, or <see langword="null"/> if the storage has no slots.</returns>
    public Hash256? ReassembleStorageTrie(in ValueHash256 accountHash)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        Hash256 address = new(accountHash);
        TreePath path = TreePath.Empty;
        SubtreeResult? result = Reassemble(reader, batch, address: address, ref path, storageRootRewrites: null);
        return result?.Hash;
    }

    private readonly record struct SubtreeResult(
        Hash256 Hash,
        NodeType Type,
        byte[]? Key,
        Hash256? InnerHash,
        byte[]? Value);

    private SubtreeResult? Reassemble(
        IPersistence.IPersistenceReader reader,
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        IReadOnlyDictionary<ValueHash256, Hash256>? storageRootRewrites)
    {
        if (path.Length > MaxPathLength)
        {
            if (_logger.IsWarn) _logger.Warn($"Reassembly recursion exceeded max depth at {path}");
            return null;
        }

        // 1) Reuse any existing trie node at this exact path.
        byte[]? existingRlp = address is null
            ? reader.TryLoadStateRlp(path, ReadFlags.None)
            : reader.TryLoadStorageRlp(address, path, ReadFlags.None);

        if (existingRlp is not null)
        {
            return ConsumeExistingNode(batch, address, ref path, existingRlp, storageRootRewrites);
        }

        // 2) Nothing at this path — probe the 16 children and recurse where any leaf descends.
        SubtreeResult[]? children = null;
        int childCount = 0;
        int lastNibble = -1;

        for (int nibble = 0; nibble < BranchChildCount; nibble++)
        {
            path.AppendMut(nibble);

            if (HasAnyLeafUnderPrefix(reader, address, in path))
            {
                SubtreeResult? child = Reassemble(reader, batch, address, ref path, storageRootRewrites);
                if (child.HasValue)
                {
                    children ??= new SubtreeResult[BranchChildCount];
                    children[nibble] = child.Value;
                    childCount++;
                    lastNibble = nibble;
                }
            }

            path.TruncateMut(path.Length - 1);
        }

        if (childCount == 0)
        {
            return null;
        }

        return childCount == 1
            ? CollapseSingleChild(batch, address, ref path, lastNibble, in children![lastNibble])
            : BuildBranch(batch, address, ref path, children!);
    }

    private SubtreeResult ConsumeExistingNode(
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        byte[] existingRlp,
        IReadOnlyDictionary<ValueHash256, Hash256>? storageRootRewrites)
    {
        TrieNode existing = new(NodeType.Unknown, existingRlp);
        existing.ResolveNode(NullTrieNodeResolver.Instance, path);

        // State leaf hitting a rewrite entry → re-encode with the new storage root.
        if (address is null
            && existing.IsLeaf
            && storageRootRewrites is not null
            && storageRootRewrites.Count > 0
            && path.Length + existing.Key!.Length == MaxPathLength)
        {
            ValueHash256 accountHash = ComputeFullPath(path, existing.Key);
            if (storageRootRewrites.TryGetValue(accountHash, out Hash256? newStorageRoot))
            {
                return RewriteStateLeaf(batch, ref path, existing, newStorageRoot!);
            }
        }

        return existing.NodeType switch
        {
            NodeType.Branch => new SubtreeResult(HashOf(existingRlp), NodeType.Branch, Key: null, InnerHash: null, Value: null),
            NodeType.Extension => new SubtreeResult(HashOf(existingRlp), NodeType.Extension, Key: existing.Key, InnerHash: existing.GetChildHash(0), Value: null),
            NodeType.Leaf => new SubtreeResult(HashOf(existingRlp), NodeType.Leaf, Key: existing.Key, InnerHash: null, Value: existing.Value.AsSpan().ToArray()),
            _ => throw new InvalidOperationException($"Unexpected node type {existing.NodeType} at {path}")
        };
    }

    /// <summary>
    /// Re-encode a leaf with a new <see cref="Account.StorageRoot"/> and persist it at the same path.
    /// </summary>
    private SubtreeResult RewriteStateLeaf(
        IPersistence.IWriteBatch batch,
        ref TreePath path,
        TrieNode existing,
        Hash256 newStorageRoot)
    {
        Account? oldAccount = _accountDecoder.Decode(existing.Value.AsSpan());
        if (oldAccount is null)
        {
            // Empty leaf value — unexpected for a state leaf; bail to caller.
            return new SubtreeResult(existing.Keccak ?? HashOf(existing.FullRlp.AsSpan().ToArray()), NodeType.Leaf, existing.Key, null, existing.Value.AsSpan().ToArray());
        }

        Account newAccount = oldAccount.WithChangedStorageRoot(newStorageRoot);
        byte[] newValueRlp = _accountDecoder.Encode(newAccount).Bytes;

        return PersistLeaf(batch, address: null, ref path, existing.Key!, newValueRlp);
    }

    private SubtreeResult CollapseSingleChild(
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        int nibble,
        in SubtreeResult child)
    {
        switch (child.Type)
        {
            case NodeType.Leaf:
                {
                    byte[] mergedKey = PrependNibble(nibble, child.Key!);
                    return PersistLeaf(batch, address, ref path, mergedKey, child.Value!);
                }
            case NodeType.Extension:
                {
                    byte[] mergedKey = PrependNibble(nibble, child.Key!);
                    return PersistExtension(batch, address, ref path, mergedKey, child.InnerHash!);
                }
            case NodeType.Branch:
                {
                    byte[] key = [(byte)nibble];
                    return PersistExtension(batch, address, ref path, key, child.Hash);
                }
            default:
                throw new InvalidOperationException($"Unexpected child type {child.Type}");
        }
    }

    private SubtreeResult BuildBranch(
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        SubtreeResult[] children)
    {
        TrieNode branch = TrieNodeFactory.CreateBranch();
        INodeData branchData = branch.NodeData!;
        for (int i = 0; i < BranchChildCount; i++)
        {
            if (children[i].Hash is not null)
            {
                branchData[i] = children[i].Hash;
            }
        }

        branch.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        WriteTrieNode(batch, address, in path, branch);

        return new SubtreeResult(branch.Keccak!, NodeType.Branch, Key: null, InnerHash: null, Value: null);
    }

    private SubtreeResult PersistLeaf(
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        byte[] keyNibbles,
        byte[] valueBytes)
    {
        LeafData leafData = new LeafData { Key = keyNibbles }.CloneWithNewValue(new CappedArray<byte>(valueBytes));
        TrieNode leaf = new(leafData);

        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        WriteTrieNode(batch, address, in path, leaf);

        return new SubtreeResult(leaf.Keccak!, NodeType.Leaf, Key: keyNibbles, InnerHash: null, Value: valueBytes);
    }

    private SubtreeResult PersistExtension(
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        byte[] keyNibbles,
        Hash256 childHash)
    {
        ExtensionData extData = new() { Key = keyNibbles, Value = childHash };
        TrieNode ext = new(extData);

        ext.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        WriteTrieNode(batch, address, in path, ext);

        return new SubtreeResult(ext.Keccak!, NodeType.Extension, Key: keyNibbles, InnerHash: childHash, Value: null);
    }

    private static void WriteTrieNode(IPersistence.IWriteBatch batch, Hash256? address, in TreePath path, TrieNode node)
    {
        if (address is null)
            batch.SetStateTrieNode(path, node);
        else
            batch.SetStorageTrieNode(address, path, node);
    }

    private static bool HasAnyLeafUnderPrefix(IPersistence.IPersistenceReader reader, Hash256? address, in TreePath prefix)
    {
        ValueHash256 lower = prefix.ToLowerBoundPath();
        ValueHash256 upper = prefix.ToUpperBoundPath();

        if (address is null)
        {
            using IPersistence.IFlatIterator it = reader.CreateAccountIterator(lower, upper);
            return it.MoveNext();
        }
        else
        {
            using IPersistence.IFlatIterator it = reader.CreateStorageIterator(address, lower, upper);
            return it.MoveNext();
        }
    }

    private static Hash256 HashOf(byte[] rlp) => new(ValueKeccak.Compute(rlp));

    /// <summary>
    /// Build the 32-byte full path from the parent's nibble path and the leaf's internal key nibbles.
    /// Caller guarantees <paramref name="path"/>.Length + <paramref name="leafKey"/>.Length == 64.
    /// </summary>
    private static ValueHash256 ComputeFullPath(in TreePath path, byte[] leafKey)
    {
        TreePath combined = path.Append(leafKey);
        return combined.Path;
    }

    private static byte[] PrependNibble(int nibble, byte[] tail)
    {
        byte[] result = new byte[tail.Length + 1];
        result[0] = (byte)nibble;
        Array.Copy(tail, 0, result, 1, tail.Length);
        return result;
    }
}
