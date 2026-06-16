// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>
/// Decorates a scope provider so that, when a scope is opened with <c>trackWitness</c>, its
/// <see cref="IWorldStateScopeProvider.IScope.Witness"/> returns the storage witness — the state-trie and
/// storage-trie node RLPs covering every account/slot the EVM touched.
/// </summary>
/// <remarks>
/// This is the only storage-coupled part of an execution witness. The touched keys and contract code are not
/// storage-coupled and are captured independently by <see cref="AccessWitnessScopeProvider"/>. The wrapper
/// tracks touched keys solely to drive its post-execution walk; it rides on the scope's own read path (the EVM
/// reads every account/slot before writing it, e.g. SSTORE reads the current value for gas), so it observes
/// writes too without any write interception. The nodes are walked once post-execution against a fresh
/// read-only view at the base state root (<paramref name="readOnlyTrieStoreFactory"/>), independent of any
/// commits done during the scope.
/// </remarks>
public class TrieWitnessScopeProvider(
    IWorldStateScopeProvider baseProvider,
    Func<IReadOnlyTrieStore> readOnlyTrieStoreFactory,
    ILogManager logManager) : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, bool trackWitness = false) =>
        trackWitness
            ? new TrieWitnessScope(baseProvider.BeginScope(baseBlock, false), baseBlock, readOnlyTrieStoreFactory, logManager)
            : baseProvider.BeginScope(baseBlock, false);

    private sealed class TrieWitnessScope(
        IWorldStateScopeProvider.IScope baseScope,
        BlockHeader? baseBlock,
        Func<IReadOnlyTrieStore> readOnlyTrieStoreFactory,
        ILogManager logManager) : IWorldStateScopeProvider.IScope
    {
        private readonly Dictionary<AddressAsKey, HashSet<UInt256>> _touchedKeys = [];
        private IReadOnlyList<byte[]>? _builtWitness;

        private void Touch(Address address)
        {
            ref HashSet<UInt256>? slots = ref CollectionsMarshal.GetValueRefOrAddDefault(_touchedKeys, address, out _);
            slots ??= [];
        }

        private void Touch(Address address, in UInt256 index)
        {
            ref HashSet<UInt256>? slots = ref CollectionsMarshal.GetValueRefOrAddDefault(_touchedKeys, address, out _);
            slots ??= [];
            slots.Add(index);
        }

        public Account? Get(Address address)
        {
            Touch(address);
            return baseScope.Get(address);
        }

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new TrieWitnessStorageTree(baseScope.CreateStorageTree(address), address, this);

        public IReadOnlyList<byte[]>? Witness => _builtWitness ??= BuildWitness();

        private IReadOnlyList<byte[]> BuildWitness()
        {
            // Walk a fresh read-only view at the base state root (flat: a fresh snapshot bundle; trie: the
            // shared read-only store) — never the live scope, which a block-level witness mutates on commit.
            IReadOnlyTrieStore roStore = readOnlyTrieStoreFactory();
            using IDisposable _ = roStore.BeginScope(baseBlock);
            return StorageWitnessCollector.Collect(
                roStore.GetTrieStore(null),
                baseBlock?.StateRoot ?? Keccak.EmptyTreeHash,
                _touchedKeys,
                logManager);
        }

        // The rest is plain delegation to the wrapped backend scope.
        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;
        public Hash256 RootHash => baseScope.RootHash;
        public void UpdateRootHash() => baseScope.UpdateRootHash();
        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);
        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => baseScope.StartWriteBatch(estimatedAccountNum);
        public void Commit(long blockNumber) => baseScope.Commit(blockNumber);
        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => baseScope.HintBal(bal, sink);
        public void Dispose() => baseScope.Dispose();

        private sealed class TrieWitnessStorageTree(IWorldStateScopeProvider.IStorageTree baseTree, Address address, TrieWitnessScope scope) : IWorldStateScopeProvider.IStorageTree
        {
            public Hash256 RootHash => baseTree.RootHash;

            public byte[] Get(in UInt256 index)
            {
                scope.Touch(address, in index);
                return baseTree.Get(in index);
            }

            public void HintSet(in UInt256 index, byte[]? value) => baseTree.HintSet(in index, value);

            // JS-tracer-only lookup by hash; not part of the standard execution witness.
            public byte[] Get(in ValueHash256 hash) => baseTree.Get(in hash);
        }
    }
}
