// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
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
/// nodes locally from the disconnected subtrees that snap sync left behind, then replays
/// EIP-7928 Block Access Lists to bridge from the first pivot snap sync downloaded against to
/// the latest pivot — avoiding the network round-trips of post-snap state healing.
/// </summary>
/// <remarks>
/// Algorithm:
///   1. Reassemble state trie (path-keyed probe-and-rebuild from snap-committed leaves), no
///      storage-root rewrites at this stage.
///   2. Reassemble each storage trie listed in the caller-supplied <c>updatedStorageAccounts</c>;
///      collect <c>(accountHash → newStorageRoot)</c> map.
///   3. Re-run state-trie reassembly with the rewrites baked into matching leaves — this is the
///      "reapply storage roots" pass, performed AFTER the state structure has been rebuilt.
///   4. Double-check by re-reading the root from disk and comparing keccak with what
///      reassembly returned.
///   5. Walk the parent chain from last pivot back to first pivot, replay each block's BAL
///      against an <see cref="IWorldStateScopeProvider.IScope"/> scoped at the first pivot.
///   6. Verify final state root against last pivot's expected root; finalize.
/// </remarks>
public sealed class FlatBalHealing(
    IPersistence persistence,
    ITreeSyncStore treeSyncStore,
    IWorldStateManager worldStateManager,
    IBlockTree blockTree,
    IBlockAccessListStore balStore,
    ISpecProvider specProvider,
    ILogManager logManager) : IBalHealing
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatBalHealing>();
    private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;

    private const int MaxPathLength = 64;
    private const int BranchChildCount = 16;

    /// <inheritdoc/>
    public Task<bool> Run(BlockHeader firstPivot, BlockHeader lastPivot, IReadOnlyCollection<Hash256> updatedStorageAccounts, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromResult(false);

        Hash256 firstRoot = firstPivot.StateRoot!;
        Hash256 expectedFinalRoot = lastPivot.StateRoot!;
        if (_logger.IsInfo) _logger.Info($"Attempting local trie reassembly with {updatedStorageAccounts.Count} updated storages, first pivot {firstPivot.Number} (root {firstRoot}), last pivot {lastPivot.Number} (root {expectedFinalRoot}).");

        // 1) Reassemble state trie (pure structural rebuild, no storage-root rewrites yet).
        // 2) Reassemble storage tries → rewrite map.
        // 3) Re-reassemble state trie with rewrites baked into matching leaves.
        Hash256? assembledRoot;
        try
        {
            assembledRoot = TryReassemble(updatedStorageAccounts);
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

        // 4) Double-check: re-read the root node from disk and recompute its keccak. Guards
        //    against the in-memory assembledRoot being right but the on-disk root not actually
        //    persisted at TreePath.Empty (e.g. a write-batch failure that didn't throw).
        if (!DoubleCheckRoot(firstRoot))
        {
            return Task.FromResult(false);
        }

        // 5) Walk the parent chain from last pivot back to first pivot. Inline so the
        //    ref-struct ArrayPoolListRef lives entirely in Run's stack frame.
        using ArrayPoolListRef<BlockHeader> chain = new(estimatedChainCapacity(firstPivot, lastPivot));
        if (lastPivot.Hash != firstPivot.Hash)
        {
            BlockHeader? cursor = lastPivot;
            while (cursor is not null && cursor.Hash != firstPivot.Hash)
            {
                chain.Add(cursor);
                cursor = blockTree.FindParentHeader(cursor, BlockTreeLookupOptions.None);
            }
            if (cursor?.Hash != firstPivot.Hash)
            {
                if (_logger.IsWarn) _logger.Warn($"BAL healing skipped: parent chain from last pivot {lastPivot.Number} does not reach first pivot {firstPivot.Number}.");
                return Task.FromResult(false);
            }
            chain.Reverse();
        }

        if (chain.Count == 0)
        {
            // first == last — snap sync's pivot never advanced. Nothing to replay; the assembled
            // trie already matches the target. Skip BeginScope/BAL apply entirely.
            if (_logger.IsInfo) _logger.Info($"Trie reassembly matches the only pivot {firstRoot}; no BAL replay needed.");
        }
        else
        {
            if (_logger.IsInfo) _logger.Info($"Trie reassembly matches first pivot {firstRoot}; replaying {chain.Count} BAL(s) to reach last pivot {lastPivot.Number}.");

            // 6) Replay BALs forward (firstPivot+1 … lastPivot) against an IScope scoped at firstPivot.
            using IWorldStateScopeProvider.IScope scope = worldStateManager.GlobalWorldState.BeginScope(firstPivot);
            foreach (BlockHeader header in chain.AsSpan())
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
                    ApplyBalToScope(scope, bal, spec);
                }
                catch (Exception e)
                {
                    if (_logger.IsWarn) _logger.Warn($"BAL replay threw at block {header.Number} {header.Hash}, falling back to healing: {e}");
                    return Task.FromResult(false);
                }
            }

            scope.UpdateRootHash();
            if (scope.RootHash != expectedFinalRoot)
            {
                if (_logger.IsWarn) _logger.Warn($"BAL replay produced {scope.RootHash}, expected last pivot {expectedFinalRoot}. Falling back to healing.");
                return Task.FromResult(false);
            }

            scope.Commit(lastPivot.Number);
            if (_logger.IsInfo) _logger.Info($"BAL replay succeeded; state at {lastPivot.Number} matches {expectedFinalRoot}.");
        }

        if (_logger.IsInfo) _logger.Info($"Finalizing sync at last pivot {lastPivot.Number} — skipping traditional healing.");
        treeSyncStore.FinalizeSync(lastPivot);

        // Diagnostic: walk the just-built trie from the last pivot root to catch any
        // missing/dangling nodes the root-hash chain might have missed. Cannot un-finalize
        // sync, but a failure is a loud signal of internal inconsistency.
        // TODO: drop this once BAL healing has been validated on mainnet.
        RunVerifyTriePostFinalize(lastPivot, expectedFinalRoot, token);

        return Task.FromResult(true);

        static int estimatedChainCapacity(BlockHeader first, BlockHeader last) =>
            last.Number > first.Number ? (int)System.Math.Min(last.Number - first.Number, 1024) : 0;
    }

    /// <summary>
    /// Re-read the root node from disk via the path-keyed store and compare its computed keccak
    /// to <paramref name="expectedRoot"/>. Returns false on mismatch (caller falls back).
    /// </summary>
    private bool DoubleCheckRoot(Hash256 expectedRoot)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        byte[]? rootRlp = reader.TryLoadStateRlp(TreePath.Empty, ReadFlags.None);
        if (rootRlp is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Root double-check failed: no node at TreePath.Empty (expected {expectedRoot}).");
            return false;
        }

        Hash256 onDiskRoot = new(ValueKeccak.Compute(rootRlp));
        if (onDiskRoot != expectedRoot)
        {
            if (_logger.IsWarn) _logger.Warn($"Root double-check failed: on-disk root {onDiskRoot} does not match assembled root {expectedRoot}.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Apply a single block's BAL to <paramref name="scope"/> via <see cref="IWorldStateScopeProvider.IWorldStateWriteBatch"/>
    /// — equivalent in result to <c>BlockAccessListManager.ApplyStateChanges</c> but operating
    /// directly on the scope instead of wrapping it in a <c>WorldState</c>.
    /// </summary>
    private static void ApplyBalToScope(IWorldStateScopeProvider.IScope scope, BlockAccessList bal, IReleaseSpec spec)
    {
        _ = spec; // currently unused; reserved for future post-EIP behaviours
        ReadOnlySpan<AccountChanges> all = bal.AccountChangesByAddress;
        using IWorldStateScopeProvider.IWorldStateWriteBatch wb = scope.StartWriteBatch(all.Length);

        foreach (AccountChanges accountChanges in all)
        {
            Address addr = accountChanges.Address;
            Account? existing = scope.Get(addr);

            UInt256 balance = existing?.Balance ?? UInt256.Zero;
            UInt256 nonce = existing?.Nonce ?? UInt256.Zero;
            Hash256 codeHash = existing?.CodeHash ?? Keccak.OfAnEmptyString;
            Hash256 storageRoot = existing?.StorageRoot ?? Keccak.EmptyTreeHash;
            bool touched = existing is not null;

            if (accountChanges.TryGetLastBalanceChangeBefore(Eip7928Constants.PrestateIndex, out BalanceChange balanceChange))
            {
                balance = balanceChange.Value;
                touched = true;
            }
            if (accountChanges.TryGetLastNonceChangeBefore(Eip7928Constants.PrestateIndex, out NonceChange nonceChange))
            {
                nonce = nonceChange.Value;
                touched = true;
            }
            if (accountChanges.TryGetLastCodeChangeBefore(Eip7928Constants.PrestateIndex, out CodeChange codeChange))
            {
                codeHash = codeChange.CodeHash.ToCommitment();
                if (codeChange.Code is { Length: > 0 })
                {
                    using IWorldStateScopeProvider.ICodeSetter codeSetter = scope.CodeDb.BeginCodeWrite();
                    codeSetter.Set(codeChange.CodeHash, codeChange.Code);
                }
                touched = true;
            }

            if (touched)
            {
                wb.Set(addr, new Account(nonce, balance, storageRoot, codeHash));
            }

            ReadOnlySpan<SlotChanges> slotsSpan = accountChanges.StorageChanges;
            if (slotsSpan.Length > 0)
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storageWb = wb.CreateStorageWriteBatch(addr, slotsSpan.Length);
                foreach (SlotChanges slotChange in slotsSpan)
                {
                    if (slotChange.Changes.TryGetLastBefore(Eip7928Constants.PrestateIndex, out StorageChange storageChange))
                    {
                        // 32-byte big-endian word, trimmed of leading zeros — same canonical
                        // representation BlockAccessListManager.ApplyStateChanges uses.
                        byte[] trimmed = MemoryMarshal
                            .CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref Unsafe.AsRef(in storageChange.Value)), 32)
                            .WithoutLeadingZeros()
                            .ToArray();
                        storageWb.Set(slotChange.Key, trimmed);
                    }
                }
            }
        }
        // wb.Dispose() (via using) flushes dirty accounts + storage-root updates to the scope.
    }

    private void RunVerifyTriePostFinalize(BlockHeader lastPivot, Hash256 expectedFinalRoot, CancellationToken token)
    {
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
    }

    /// <summary>
    /// Three-pass reassembly: (1) state trie structure, (2) storage tries → rewrite map,
    /// (3) state trie with leaf storage-root rewrites baked in.
    /// Pass 1 walks the snap-committed leaves and rebuilds the missing spine; pass 3
    /// re-walks the same structure but rewrites any state leaves matching <paramref name="updatedStorageAccounts"/>
    /// so their <see cref="Account.StorageRoot"/> reflects the freshly assembled storage roots.
    /// Pass 1 is the "state root rebuild"; pass 3 is the "reapply storage roots" step done
    /// AFTER the rebuild — separating the two concerns at the API level even though pass 3 is
    /// implemented as a second reassembly with the rewrites bound in.
    /// </summary>
    public Hash256? TryReassemble(IReadOnlyCollection<Hash256> updatedStorageAccounts)
    {
        // Pass 1: rebuild state trie structurally (no storage rewrites).
        Hash256? baseRoot = ReassembleStateTrie(storageRootRewrites: null);
        if (baseRoot is null)
        {
            if (_logger.IsInfo) _logger.Info("Trie reassembly: state DB has no leaves — nothing to assemble.");
            return null;
        }

        // Pass 2: rebuild storage tries; collect new storage roots per touched account.
        Dictionary<ValueHash256, Hash256> rewrites = new(updatedStorageAccounts.Count);
        foreach (Hash256 accountHash in updatedStorageAccounts)
        {
            Hash256? newRoot = ReassembleStorageTrie(accountHash.ValueHash256);
            if (newRoot is not null)
            {
                rewrites[accountHash.ValueHash256] = newRoot;
            }
        }

        if (rewrites.Count == 0)
        {
            if (_logger.IsInfo) _logger.Info($"Trie reassembly: no storage rewrites; base state root {baseRoot} is final.");
            return baseRoot;
        }

        // Pass 3: reapply storage roots into state-trie leaves.
        if (_logger.IsInfo) _logger.Info($"Trie reassembly: applying {rewrites.Count} storage-root rewrites on top of base state root {baseRoot}.");
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
