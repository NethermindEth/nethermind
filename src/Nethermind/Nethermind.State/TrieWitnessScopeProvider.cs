// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>
/// Decorates a scope provider so that, when a scope is opened with <c>trackWitness</c>, its
/// <see cref="IWorldStateScopeProvider.IScope.Witness"/> returns the storage witness — the state-trie and
/// storage-trie node RLPs a stateless verifier needs to re-execute the block's reads and recompute the
/// post-state root.
/// </summary>
/// <remarks>
/// This is the only storage-coupled part of an execution witness; the touched keys and contract code are
/// captured independently by <see cref="AccessWitnessScopeProvider"/>. The wrapper records, per account/slot,
/// whether it was read or deleted: reads ride on the scope's own read path (the EVM reads every account/slot
/// before writing), and deletes (SSTORE→0, SELFDESTRUCT, EIP-158 prune) are observed by decorating the commit
/// write batch. After execution, <see cref="PatriciaTrieWitnessGenerator"/> walks a fresh read-only view of the
/// base state — once over the state trie and once per touched account's storage trie — reporting both the
/// read-proof nodes and the collapse siblings that deletions pull in. The backend does no witness bookkeeping.
/// </remarks>
public class TrieWitnessScopeProvider(
    IWorldStateScopeProvider baseProvider,
    Func<IReadOnlyTrieStore> readOnlyTrieStoreFactory,
    ILogManager logManager) : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, bool trackWitness = false) =>
        trackWitness
            // The backend no longer captures anything; only this wrapper builds the witness.
            ? new TrieWitnessScope(baseProvider.BeginScope(baseBlock, false), baseBlock, readOnlyTrieStoreFactory, logManager)
            : baseProvider.BeginScope(baseBlock, false);

    /// <summary>How a key path was touched. Only <see cref="Delete"/> can collapse a branch.</summary>
    private enum Access : byte { Read, Delete }

    private sealed class TouchedAccount
    {
        public Access Access;            // account-level: Delete iff removed (Set null)
        public bool StorageCleared;      // Clear(): whole storage trie dropped, no storage walk needed
        public readonly Dictionary<UInt256, Access> Slots = [];
    }

    private sealed class TrieWitnessScope(
        IWorldStateScopeProvider.IScope baseScope,
        BlockHeader? baseBlock,
        Func<IReadOnlyTrieStore> readOnlyTrieStoreFactory,
        ILogManager logManager) : IWorldStateScopeProvider.IScope
    {
        private readonly Dictionary<AddressAsKey, TouchedAccount> _touched = [];
        private IReadOnlyList<byte[]>? _builtWitness;

        private TouchedAccount Touch(Address address)
        {
            ref TouchedAccount? acct = ref CollectionsMarshal.GetValueRefOrAddDefault(_touched, address, out _);
            return acct ??= new TouchedAccount();
        }

        private void TouchSlot(Address address, in UInt256 index)
        {
            Dictionary<UInt256, Access> slots = Touch(address).Slots;
            ref Access slot = ref CollectionsMarshal.GetValueRefOrAddDefault(slots, index, out bool existed);
            if (!existed) slot = Access.Read;
        }

        // Account/slot deletes observed on the commit write path; the tag is monotonic (Read -> Delete only).
        private void MarkAccount(Address address, Account? account)
        {
            TouchedAccount acct = Touch(address);
            if (account is null) acct.Access = Access.Delete;
        }

        private void MarkSlot(Address address, in UInt256 index, byte[] value)
        {
            Dictionary<UInt256, Access> slots = Touch(address).Slots;
            slots[index] = value.IsZero() ? Access.Delete : Access.Read;
        }

        private void MarkStorageCleared(Address address) => Touch(address).StorageCleared = true;

        public Account? Get(Address address)
        {
            Touch(address);
            return baseScope.Get(address);
        }

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new TrieWitnessStorageTree(baseScope.CreateStorageTree(address), address, this);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new TrieWitnessWriteBatch(baseScope.StartWriteBatch(estimatedAccountNum), this);

        public IReadOnlyList<byte[]>? Witness => _builtWitness ??= BuildWitness();

        private IReadOnlyList<byte[]> BuildWitness()
        {
            // Walk a fresh read-only view at the base state root — never the live scope, which a block-level
            // witness mutates — so the result reflects the pre-execution state regardless of commits done here.
            IReadOnlyTrieStore roStore = readOnlyTrieStoreFactory();
            using IDisposable _ = roStore.BeginScope(baseBlock);
            Hash256 stateRoot = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;

            RlpCollectingSink sink = new();
            IScopedTrieStore stateResolver = roStore.GetTrieStore(null);

            // State trie: one entry per touched account, deletes (SELFDESTRUCT / EIP-158 prune) tagged so the
            // generator pulls in the collapse sibling.
            PatriciaTrieWitnessGenerator.Generate(stateResolver, stateRoot, BuildAccountPaths(), sink);

            if (stateRoot != Keccak.EmptyTreeHash)
            {
                // Per-account storage trees. The base storage root must come from the pre-state view.
                StateTree baseStateView = new(stateResolver, logManager) { RootHash = stateRoot };
                foreach ((AddressAsKey addr, TouchedAccount t) in _touched)
                {
                    // A self-destructed account drops its whole storage trie; only the state-trie account-delete
                    // collapse (covered above) matters.
                    if (t.StorageCleared || t.Slots.Count == 0) continue;

                    Hash256 storageRoot = baseStateView.Get(addr.Value)?.StorageRoot ?? Keccak.EmptyTreeHash;
                    if (storageRoot == Keccak.EmptyTreeHash) continue; // freshly created this block: no pre-state storage

                    IScopedTrieStore storageResolver = roStore.GetTrieStore(addr.Value);
                    PatriciaTrieWitnessGenerator.Generate(storageResolver, storageRoot, BuildSlotPaths(t.Slots), sink);
                }
            }

            return sink.Nodes;
        }

        private PatriciaTrieWitnessGenerator.PathEntry[] BuildAccountPaths()
        {
            PatriciaTrieWitnessGenerator.PathEntry[] paths = new PatriciaTrieWitnessGenerator.PathEntry[_touched.Count];
            int i = 0;
            foreach ((AddressAsKey addr, TouchedAccount t) in _touched)
                paths[i++] = new(addr.Value.ToAccountPath, ToAccessType(t.Access));
            return paths;
        }

        private static PatriciaTrieWitnessGenerator.PathEntry[] BuildSlotPaths(Dictionary<UInt256, Access> slots)
        {
            PatriciaTrieWitnessGenerator.PathEntry[] paths = new PatriciaTrieWitnessGenerator.PathEntry[slots.Count];
            int i = 0;
            Span<byte> slotKey = stackalloc byte[32];
            foreach ((UInt256 slot, Access access) in slots)
            {
                slot.ToBigEndian(slotKey);
                paths[i++] = new(ValueKeccak.Compute(slotKey), ToAccessType(access));
            }
            return paths;
        }

        private static PatriciaTrieWitnessGenerator.AccessType ToAccessType(Access access) =>
            access == Access.Delete ? PatriciaTrieWitnessGenerator.AccessType.Delete : PatriciaTrieWitnessGenerator.AccessType.Read;

        // The rest is plain delegation to the wrapped backend scope.
        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;
        public Hash256 RootHash => baseScope.RootHash;
        public void UpdateRootHash() => baseScope.UpdateRootHash();
        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);
        public void Commit(long blockNumber) => baseScope.Commit(blockNumber);
        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => baseScope.HintBal(bal, sink);
        public void Dispose() => baseScope.Dispose();

        private sealed class TrieWitnessStorageTree(IWorldStateScopeProvider.IStorageTree baseTree, Address address, TrieWitnessScope scope) : IWorldStateScopeProvider.IStorageTree
        {
            public Hash256 RootHash => baseTree.RootHash;

            public byte[] Get(in UInt256 index)
            {
                scope.TouchSlot(address, in index);
                return baseTree.Get(in index);
            }

            public void HintSet(in UInt256 index, byte[]? value) => baseTree.HintSet(in index, value);

            // JS-tracer-only lookup by hash; not part of the standard execution witness.
            public byte[] Get(in ValueHash256 hash) => baseTree.Get(in hash);
        }

        private sealed class TrieWitnessWriteBatch(IWorldStateScopeProvider.IWorldStateWriteBatch baseBatch, TrieWitnessScope scope) : IWorldStateScopeProvider.IWorldStateWriteBatch
        {
            public event EventHandler<IWorldStateScopeProvider.AccountUpdated> OnAccountUpdated
            {
                add => baseBatch.OnAccountUpdated += value;
                remove => baseBatch.OnAccountUpdated -= value;
            }

            public void Set(Address key, Account? account)
            {
                scope.MarkAccount(key, account);
                baseBatch.Set(key, account);
            }

            public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
                new TrieWitnessStorageWriteBatch(baseBatch.CreateStorageWriteBatch(key, estimatedEntries), key, scope);

            public void Dispose() => baseBatch.Dispose();
        }

        private sealed class TrieWitnessStorageWriteBatch(IWorldStateScopeProvider.IStorageWriteBatch baseBatch, Address address, TrieWitnessScope scope) : IWorldStateScopeProvider.IStorageWriteBatch
        {
            public void Set(in UInt256 index, byte[] value)
            {
                scope.MarkSlot(address, in index, value);
                baseBatch.Set(in index, value);
            }

            public void Clear()
            {
                scope.MarkStorageCleared(address);
                baseBatch.Clear();
            }

            public void Dispose() => baseBatch.Dispose();
        }

        private sealed class RlpCollectingSink : PatriciaTrieWitnessGenerator.ISink
        {
            // The generator reports each standalone node once (content-addressed), so no dedup is needed.
            private readonly List<byte[]> _nodes = [];
            public void Add(in TreePath path, TrieNode node) => _nodes.Add(node.FullRlp.ToArray());
            public IReadOnlyList<byte[]> Nodes => _nodes;
        }
    }
}
