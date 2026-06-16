// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>
/// Decorates a scope provider so that, when a scope is opened with <c>trackWitness</c>, it records every
/// account and storage slot the EVM touches and every contract code it reads, and exposes the resulting
/// <see cref="ScopeWitness"/> through <see cref="IWorldStateScopeProvider.IScope.Witness"/>.
/// </summary>
/// <remarks>
/// All witness bookkeeping lives here rather than in the backend (flat/trie) scopes, so the storage backends
/// stay free of witness concerns. Recording rides entirely on the scope's own read path: the EVM reads every
/// account and slot before writing it (e.g. SSTORE reads the current value for gas), so the wrapper's
/// <see cref="WitnessScope.Get"/> / storage-tree <c>Get</c> / code-db <c>GetCode</c> observe writes too — no
/// separate write interception is needed, and none would suffice for a <c>CallAndRestore</c> (proof_call) that
/// never commits or for reverted writes. The witness state nodes are produced post-execution by walking a
/// fresh read-only view at the base state root (<paramref name="readOnlyTrieStoreFactory"/>), independent of
/// any commits done during the scope.
/// </remarks>
public class WitnessScopeProvider(
    IWorldStateScopeProvider baseProvider,
    Func<IReadOnlyTrieStore> readOnlyTrieStoreFactory,
    ILogManager logManager) : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, bool trackWitness = false) =>
        trackWitness
            ? new WitnessScope(baseProvider.BeginScope(baseBlock, false), baseBlock, readOnlyTrieStoreFactory, logManager)
            : baseProvider.BeginScope(baseBlock, false);

    private sealed class WitnessScope : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope _baseScope;
        private readonly BlockHeader? _baseBlock;
        private readonly Func<IReadOnlyTrieStore> _readOnlyTrieStoreFactory;
        private readonly ILogManager _logManager;

        private readonly Dictionary<AddressAsKey, HashSet<UInt256>> _touchedKeys = [];
        private readonly Dictionary<ValueHash256, byte[]> _bytecodes =
            new(GenericEqualityComparer.GetOptimized<ValueHash256>());
        private readonly WitnessCodeDb _codeDb;
        private ScopeWitness? _builtWitness;

        public WitnessScope(IWorldStateScopeProvider.IScope baseScope, BlockHeader? baseBlock, Func<IReadOnlyTrieStore> readOnlyTrieStoreFactory, ILogManager logManager)
        {
            _baseScope = baseScope;
            _baseBlock = baseBlock;
            _readOnlyTrieStoreFactory = readOnlyTrieStoreFactory;
            _logManager = logManager;
            _codeDb = new WitnessCodeDb(baseScope.CodeDb, _bytecodes);
        }

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
            return _baseScope.Get(address);
        }

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new WitnessStorageTree(_baseScope.CreateStorageTree(address), address, this);

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;

        public ScopeWitness? Witness => _builtWitness ??= BuildWitness();

        private ScopeWitness BuildWitness()
        {
            // Walk a fresh read-only view at the base state root (flat: a fresh snapshot bundle; trie: the
            // shared read-only store) — never the live scope, which a block-level witness mutates on commit.
            IReadOnlyTrieStore roStore = _readOnlyTrieStoreFactory();
            using IDisposable _ = roStore.BeginScope(_baseBlock);
            return StorageWitnessCollector.Collect(
                roStore.GetTrieStore(null),
                _baseBlock?.StateRoot ?? Keccak.EmptyTreeHash,
                _touchedKeys,
                _bytecodes.Values,
                _logManager);
        }

        // The rest is plain delegation to the wrapped backend scope.
        public Hash256 RootHash => _baseScope.RootHash;
        public void UpdateRootHash() => _baseScope.UpdateRootHash();
        public void HintGet(Address address, Account? account) => _baseScope.HintGet(address, account);
        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => _baseScope.StartWriteBatch(estimatedAccountNum);
        public void Commit(long blockNumber) => _baseScope.Commit(blockNumber);
        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => _baseScope.HintBal(bal, sink);
        public void Dispose() => _baseScope.Dispose();

        private sealed class WitnessStorageTree(IWorldStateScopeProvider.IStorageTree baseTree, Address address, WitnessScope scope) : IWorldStateScopeProvider.IStorageTree
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

    private sealed class WitnessCodeDb(IWorldStateScopeProvider.ICodeDb baseCodeDb, Dictionary<ValueHash256, byte[]> bytecodes) : IWorldStateScopeProvider.ICodeDb
    {
        public byte[]? GetCode(in ValueHash256 codeHash)
        {
            byte[]? code = baseCodeDb.GetCode(in codeHash);
            // Empty code is never part of a witness; deployed-this-block code is served from the state
            // provider's batch and never reaches here, so it is correctly excluded.
            if (code is { Length: > 0 }) bytecodes.TryAdd(codeHash, code);
            return code;
        }

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => baseCodeDb.BeginCodeWrite();
        public bool ContainsCode(in ValueHash256 codeHash) => baseCodeDb.ContainsCode(in codeHash);
        public void MarkCodePersisted(in ValueHash256 codeHash) => baseCodeDb.MarkCodePersisted(in codeHash);
    }
}
