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
/// account/slot touched (via <see cref="IWorldStateScopeProvider.IScope.ReportRead(Address)"/> /
/// <see cref="IWorldStateScopeProvider.IScope.ReportRead(in StorageCell)"/>) and every contract code read,
/// and exposes the resulting <see cref="ScopeWitness"/> through <see cref="IWorldStateScopeProvider.IScope.Witness"/>.
/// </summary>
/// <remarks>
/// All witness bookkeeping lives here rather than in the backend (flat/trie) scopes, so the storage backends
/// stay free of witness concerns. Reads/writes are reported from the layers above the scope
/// (<see cref="StateProvider"/> / <see cref="PersistentStorageProvider"/>) because their per-block caches sit
/// above the scope and writes never read through it — a scope-level wrapper alone cannot observe those touches.
/// The witness state nodes are produced post-execution by walking a fresh read-only view at the base state
/// root (<paramref name="readOnlyTrieStoreFactory"/>), independent of any commits done during the scope.
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

        public void ReportRead(Address address)
        {
            ref HashSet<UInt256>? slots = ref CollectionsMarshal.GetValueRefOrAddDefault(_touchedKeys, address, out _);
            slots ??= [];
        }

        public void ReportRead(in StorageCell storageCell)
        {
            ref HashSet<UInt256>? slots = ref CollectionsMarshal.GetValueRefOrAddDefault(_touchedKeys, storageCell.Address, out _);
            slots ??= [];
            slots.Add(storageCell.Index);
        }

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

        // The rest is plain delegation to the wrapped backend scope, except CodeDb which is wrapped to
        // capture bytecode reads.
        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;
        public Hash256 RootHash => _baseScope.RootHash;
        public void UpdateRootHash() => _baseScope.UpdateRootHash();
        public Account? Get(Address address) => _baseScope.Get(address);
        public void HintGet(Address address, Account? account) => _baseScope.HintGet(address, account);
        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => _baseScope.CreateStorageTree(address);
        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => _baseScope.StartWriteBatch(estimatedAccountNum);
        public void Commit(long blockNumber) => _baseScope.Commit(blockNumber);
        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => _baseScope.HintBal(bal, sink);
        public void Dispose() => _baseScope.Dispose();
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
