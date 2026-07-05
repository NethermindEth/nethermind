// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
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
public sealed class BalStateRootCalculator
{
    /// <summary>
    /// Batched path only: minimum number of storage-writing accounts before independent storage tries are BUILT on
    /// several cores instead of sequentially. Below it the calling thread builds all storage tries in order. The merged
    /// wave that hashes them is a single dispatch either way; this threshold gates only the parallel trie construction.
    /// </summary>
    internal const int DefaultStorageTrieParallelThreshold = 8;

    private readonly ITrieStore _trieStore;
    private readonly ILogManager _logManager;
    private readonly int _storageTrieParallelThreshold;

    /// <summary>Creates a calculator over the given (read-only) trie store.</summary>
    /// <param name="trieStore">The read-only trie store the pre-state is resolved through; it must clone nodes.</param>
    /// <param name="logManager">Log manager.</param>
    public BalStateRootCalculator(ITrieStore trieStore, ILogManager logManager)
        : this(trieStore, logManager, DefaultStorageTrieParallelThreshold)
    {
    }

    /// <summary>Creates a calculator with an explicit across-storage-tries parallelization threshold (batched path only).</summary>
    /// <param name="storageTrieParallelThreshold">Minimum storage-writing account count before storage tries are BUILT across cores; injectable so tests can exercise the parallel build path on small deltas.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="storageTrieParallelThreshold"/> is less than 1.</exception>
    internal BalStateRootCalculator(ITrieStore trieStore, ILogManager logManager, int storageTrieParallelThreshold)
    {
        ArgumentNullException.ThrowIfNull(trieStore);
        ArgumentNullException.ThrowIfNull(logManager);
        ArgumentOutOfRangeException.ThrowIfLessThan(storageTrieParallelThreshold, 1);
        _trieStore = trieStore;
        _logManager = logManager;
        _storageTrieParallelThreshold = storageTrieParallelThreshold;
    }

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
    /// <remarks>Same three passes as the recursive overload; only the root-hashing step differs: storage roots are computed as one merged cross-trie wave with <paramref name="hasher"/> (the threshold gates parallel trie CONSTRUCTION only, not the hashing, which is a single dispatch per wave step), and the state tree is hashed with the same hasher.</remarks>
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
        // Scalar composition and the EIP-161 deletion decision stay sequential; only the independent
        // storage-tree hashing (the expensive part) may run across cores on the batched path.
        Account?[] composed = new Account?[n];
        Hash256[] storageRoots = new Hash256[n]; // storageRoots[i] valid only for survivors with storage writes
        int[]? withStorageWrites = null; // indices needing a storage-root computation, filled lazily
        int withStorageCount = 0;
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

            if (ad.Storage.Length == 0)
            {
                // No storage writes: keep the pre-state storage root; the account is fully composed now.
                Hash256 storageRoot = p?.StorageRoot ?? PatriciaTree.EmptyTreeHash;
                composed[i] = new Account(nonce, balance, storageRoot, codeHash);
            }
            else
            {
                // Defer the storage-root computation; compose after it is known (below).
                composed[i] = new Account(nonce, balance, PatriciaTree.EmptyTreeHash, codeHash);
                (withStorageWrites ??= new int[n])[withStorageCount++] = i;
            }
        }

        // Compute deferred storage roots. Each storage trie is independent (its own address-scoped read-only store).
        // Batched path: build every storage trie (slot writes), then hash them ALL in one merged cross-trie wave with the
        // caller hasher - the widest possible dispatch (all leaf levels of all tries together), the shape GPU/SIMD need.
        // Recursive path: hash each trie sequentially in place.
        if (withStorageCount > 0)
        {
            if (hasher is not null)
            {
                ComputeStorageRootsMergedWave(accounts, pre, withStorageWrites!, withStorageCount, storageRoots, hasher);
            }
            else
            {
                for (int k = 0; k < withStorageCount; k++)
                {
                    int i = withStorageWrites![k];
                    storageRoots[i] = ComputeStorageRoot(in accounts[i], pre[i], hasher);
                }
            }

            for (int k = 0; k < withStorageCount; k++)
            {
                int i = withStorageWrites![k];
                composed[i] = composed[i]!.WithChangedStorageRoot(storageRoots[i]);
            }
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

    /// <summary>Builds account <paramref name="ad"/>'s storage tree from its post-block slot writes and returns its root.</summary>
    private Hash256 ComputeStorageRoot(in BalPostStateDelta.AccountDelta ad, Account? pre, IKeccakBatchHasher? hasher)
    {
        StorageTree storageTree = BuildStorageTree(in ad, pre);
        UpdateRoot(storageTree, hasher);
        return storageTree.RootHash;
    }

    /// <summary>Builds account <paramref name="ad"/>'s storage tree from its post-block slot writes WITHOUT hashing it.</summary>
    /// <remarks>Split from <see cref="ComputeStorageRoot"/> so the merged wave can build all tries first, then hash them together.</remarks>
    private StorageTree BuildStorageTree(in BalPostStateDelta.AccountDelta ad, Account? pre)
    {
        Hash256 preStorageRoot = pre?.StorageRoot ?? PatriciaTree.EmptyTreeHash;
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
        return storageTree;
    }

    /// <summary>
    /// Computes all deferred storage roots as ONE merged cross-trie wave: build every storage trie, then hash them all
    /// together with the caller <paramref name="hasher"/> so each wave step's batch spans every trie's level at once.
    /// </summary>
    /// <remarks>
    /// The merged wave (<see cref="BatchedTrieCommitter.UpdateRootHashesBatched(IReadOnlyList{PatriciaTree}, IKeccakBatchHasher)"/>)
    /// is what produces the tens-of-thousands-wide dispatches the GPU threshold needs - each storage trie is independent,
    /// so the level barrier is only per-trie and every trie's leaf level joins the first wave steps. It also equals the
    /// single-trie batched path when only one trie has writes. Trie construction (the slot-write application) is
    /// independent per trie, so above <see cref="_storageTrieParallelThreshold"/> it runs across cores, largest-first so
    /// long tries start early; below it, tries are built sequentially. In both cases the wave hashing itself is a single
    /// call and remains a single batch per step.
    /// <para>
    /// Thread-safety of the parallel BUILD rests on trie independence: each <see cref="StorageTree"/> is over its own
    /// address-scoped read-only store, so distinct tries never resolve the same node, and <see cref="StorageTree.Set"/>
    /// is copy-on-write on sealed nodes. The <c>BeginScope</c> opened before this call is only read through concurrently.
    /// The wave runs AFTER the build join, on the calling thread, so the caller hasher's own parallelism is not nested
    /// inside a build parallel region.
    /// </para>
    /// </remarks>
    private void ComputeStorageRootsMergedWave(
        BalPostStateDelta.AccountDelta[] accounts,
        Account?[] pre,
        int[] withStorageWrites,
        int withStorageCount,
        Hash256[] storageRoots,
        IKeccakBatchHasher hasher)
    {
        // Largest-first: build order by descending slot-write count so long tries do not straggle in the parallel build.
        int[] order = new int[withStorageCount];
        Array.Copy(withStorageWrites, order, withStorageCount);
        Array.Sort(order, (a, b) => accounts[b].Storage.Length.CompareTo(accounts[a].Storage.Length));

        StorageTree[] trees = new StorageTree[withStorageCount];
        if (withStorageCount >= _storageTrieParallelThreshold)
        {
            ParallelUnbalancedWork.For(
                0,
                withStorageCount,
                Nethermind.Core.Cpu.RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                w => trees[w] = BuildStorageTree(in accounts[order[w]], pre[order[w]]));
        }
        else
        {
            for (int w = 0; w < withStorageCount; w++)
            {
                trees[w] = BuildStorageTree(in accounts[order[w]], pre[order[w]]);
            }
        }

        // One merged wave over every built trie with the caller hasher: the widest possible dispatch.
        BatchedTrieCommitter.UpdateRootHashesBatched(trees, hasher);

        for (int w = 0; w < withStorageCount; w++)
        {
            storageRoots[order[w]] = trees[w].RootHash;
        }
    }
}
