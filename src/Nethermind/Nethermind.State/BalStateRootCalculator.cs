// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>Computes the post-block state root from a BAL-derived delta without executing the block.</summary>
/// <remarks>
/// Read-only: resolves pre-state through the injected trie store (which must be a read-only store),
/// hashes into cloned nodes, and never commits. Node eviction under pruning and a missing state at the
/// parent both surface as throws from <see cref="StateTree.Get(Address, Hash256?)"/> or
/// <see cref="ITrieStore.BeginScope(BlockHeader?)"/>; callers wrap the whole call.
/// </remarks>
public sealed class BalStateRootCalculator(ITrieStore trieStore, ILogManager logManager)
{
    private readonly ITrieStore _trieStore = trieStore;
    private readonly ILogManager _logManager = logManager;

    /// <summary>Computes the state root that a block would produce given its parent header and BAL delta.</summary>
    /// <param name="parent">The parent block header; its <see cref="BlockHeader.StateRoot"/> is the pre-state root.</param>
    /// <param name="delta">The reduced post-block state delta (see <see cref="BalPostStateDelta"/>).</param>
    /// <returns>The computed post-block state root.</returns>
    /// <remarks>
    /// Three strict passes: (A) all pre-state reads before any mutation, so a later read never observes a
    /// partially-updated tree; (B) storage roots and account composition, with EIP-161 deletion decided by
    /// <see cref="Account.IsEmpty"/> semantics (the storage root is deliberately not consulted); (C) state-tree
    /// writes and a single root computation. Never commits: only <see cref="PatriciaTree.UpdateRootHash(bool)"/>.
    /// </remarks>
    public Hash256 ComputeRoot(BlockHeader parent, BalPostStateDelta delta) => ComputeRootInternal(parent, delta, hasher: null);

    /// <summary>
    /// Computes the post-block state root, hashing every storage tree and the state tree via
    /// <see cref="BatchedTrieCommitter.UpdateRootHashBatched"/> with the given batch hasher instead of the recursive
    /// per-node path.
    /// </summary>
    /// <param name="parent">The parent block header; its <see cref="BlockHeader.StateRoot"/> is the pre-state root.</param>
    /// <param name="delta">The reduced post-block state delta (see <see cref="BalPostStateDelta"/>).</param>
    /// <param name="hasher">Batch hasher used for the wave merkleization.</param>
    /// <returns>The computed post-block state root; identical to the recursive overload.</returns>
    /// <remarks>Same three passes as the recursive overload; only the root-hashing step differs. Sequential (no across-tries parallelism).</remarks>
    public Hash256 ComputeRoot(BlockHeader parent, BalPostStateDelta delta, IKeccakBatchHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        return ComputeRootInternal(parent, delta, hasher);
    }

    private Hash256 ComputeRootInternal(BlockHeader parent, BalPostStateDelta delta, IKeccakBatchHasher? hasher)
    {
        using IDisposable _ = _trieStore.BeginScope(parent); // no-op on halfpath; REQUIRED on flat
        Hash256 parentStateRoot = parent.StateRoot!;
        StateTree stateTree = new(_trieStore.GetTrieStore(null), _logManager); // IScopedTrieStore ctor sets TrieType.State

        BalPostStateDelta.AccountDelta[] accounts = delta.Accounts;
        int n = accounts.Length;

        // PASS A: all pre-state reads before any mutation. Explicit root ignores RootRef; interleaving
        // reads with Sets would observe a partially-updated tree. An explicit-root Get on EmptyTreeHash
        // would THROW (the empty-tree node is never stored), so the guard short-circuits to null instead.
        Account?[] pre = new Account?[n];
        bool emptyParent = parentStateRoot == PatriciaTree.EmptyTreeHash;
        for (int i = 0; i < n; i++)
        {
            pre[i] = emptyParent ? null : stateTree.Get(accounts[i].Address, parentStateRoot); // THROWS on evicted node - caller catches
        }

        // PASS B: compose accounts; storage roots only for non-empty survivors.
        Account?[] composed = new Account?[n];
        for (int i = 0; i < n; i++)
        {
            BalPostStateDelta.AccountDelta ad = accounts[i];
            Account? p = pre[i];

            ulong nonce = ad.Nonce ?? p?.Nonce ?? 0UL;
            UInt256 balance = ad.Balance ?? p?.Balance ?? UInt256.Zero;
            Hash256 codeHash = ad.CodeHash is { } vh ? new Hash256(vh) : (p?.CodeHash ?? Keccak.OfAnEmptyString);

            // EIP-161: matches Account.IsEmpty / StateProvider deletion - the storage root is NOT consulted.
            if (nonce == 0 && balance.IsZero && codeHash == Keccak.OfAnEmptyString)
            {
                composed[i] = null; // delete leaf; orphans any storage subtree
                continue;
            }

            Hash256 storageRoot;
            if (ad.Storage.Length == 0)
            {
                storageRoot = p?.StorageRoot ?? PatriciaTree.EmptyTreeHash;
            }
            else
            {
                Hash256 preStorageRoot = p?.StorageRoot ?? PatriciaTree.EmptyTreeHash;
                StorageTree storageTree = new(_trieStore.GetTrieStore(ad.Address), preStorageRoot, _logManager);
                foreach (BalPostStateDelta.SlotWrite slot in ad.Storage)
                {
                    UInt256 slotKey = slot.Slot;
                    EvmWord wv = slot.Value; // mutable local: ref needs an lvalue
                    ReadOnlySpan<byte> value = MemoryMarshal
                        .CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref wv), 32)
                        .WithoutLeadingZeros();
                    storageTree.Set(in slotKey, value.ToArray()); // all-zero -> [0] -> IsZero -> leaf delete
                }
                UpdateRoot(storageTree, hasher);
                storageRoot = storageTree.RootHash;
            }

            composed[i] = new Account(nonce, balance, storageRoot, codeHash);
        }

        // PASS C: state-tree writes, then one root computation. Never Commit.
        stateTree.SetRootHash(parentStateRoot, true);
        using (StateTree.StateTreeBulkSetter setter = stateTree.BeginSet(n))
        {
            for (int i = 0; i < n; i++)
            {
                setter.Set(accounts[i].Address, composed[i]);
            }
        }
        UpdateRoot(stateTree, hasher);
        return stateTree.RootHash;
    }

    /// <summary>Finalizes a tree's root hash: recursive when <paramref name="hasher"/> is null, batched otherwise.</summary>
    private static void UpdateRoot(PatriciaTree tree, IKeccakBatchHasher? hasher)
    {
        if (hasher is null)
        {
            tree.UpdateRootHash(canBeParallel: false);
        }
        else
        {
            BatchedTrieCommitter.UpdateRootHashBatched(tree, hasher);
        }
    }
}
