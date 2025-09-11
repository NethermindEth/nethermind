// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Ethereum.Test.Base")]
[assembly: InternalsVisibleTo("Ethereum.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.State.Test")]
[assembly: InternalsVisibleTo("Nethermind.Benchmark")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization")]

namespace Nethermind.State
{
    public class WorldState : IWorldState, IPreBlockCaches
    {
        internal readonly StateProvider _stateProvider;
        internal readonly PersistentStorageProvider _persistentStorageProvider;
        private readonly TransientStorageProvider _transientStorageProvider;
        private readonly ITrieStore _trieStore;
        private bool _isInScope = false;
        private readonly ILogger _logger;
        private PreBlockCaches? PreBlockCaches { get; }
        public bool IsWarmWorldState { get; }

        public Hash256 StateRoot
        {
            get
            {
                GuardInScope();
                return _stateProvider.StateRoot;
            }
            private set
            {
                _stateProvider.StateRoot = value;
                _persistentStorageProvider.StateRoot = value;
            }
        }

        public WorldState(ITrieStore trieStore, IKeyValueStoreWithBatching? codeDb, ILogManager? logManager)
            : this(trieStore, codeDb, logManager, null, null)
        {
        }

        internal WorldState(
            ITrieStore trieStore,
            IKeyValueStoreWithBatching? codeDb,
            ILogManager? logManager,
            StateTree? stateTree = null,
            IStorageTreeFactory? storageTreeFactory = null,
            PreBlockCaches? preBlockCaches = null,
            bool populatePreBlockCache = true)
        {
            PreBlockCaches = preBlockCaches;
            IsWarmWorldState = !populatePreBlockCache;
            _trieStore = trieStore;
            _stateProvider = new StateProvider(trieStore.GetTrieStore(null), codeDb, logManager, stateTree, PreBlockCaches?.StateCache, populatePreBlockCache);
            _persistentStorageProvider = new PersistentStorageProvider(trieStore, _stateProvider, logManager, storageTreeFactory, PreBlockCaches?.StorageCache, populatePreBlockCache);
            _transientStorageProvider = new TransientStorageProvider(logManager);
            _logger = logManager.GetClassLogger<WorldState>();
        }

        public WorldState(ITrieStore trieStore, IKeyValueStoreWithBatching? codeDb, ILogManager? logManager, PreBlockCaches? preBlockCaches, bool populatePreBlockCache = true)
            : this(trieStore, codeDb, logManager, null, preBlockCaches: preBlockCaches, populatePreBlockCache: populatePreBlockCache)
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GuardInScope()
        {
            if (!_isInScope) ThrowOutOfScope();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DebugGuardInScope()
        {
#if DEBUG
            if (!_isInScope) ThrowOutOfScope();
#endif
        }

        [StackTraceHidden, DoesNotReturn]
        private void ThrowOutOfScope()
        {
            throw new InvalidOperationException($"{nameof(IWorldState)} must only be used within scope");
        }

        public Account GetAccount(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.GetAccount(address);
        }

        bool IAccountStateProvider.TryGetAccount(Address address, out AccountStruct account)
        {
            account = _stateProvider.GetAccount(address).ToStruct();
            return !account.IsTotallyEmpty;
        }

        public bool IsContract(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.IsContract(address);
        }

        public byte[] GetOriginal(in StorageCell storageCell)
        {
            DebugGuardInScope();
            return _persistentStorageProvider.GetOriginal(storageCell);
        }
        public ReadOnlySpan<byte> Get(in StorageCell storageCell)
        {
            DebugGuardInScope();
            return _persistentStorageProvider.Get(storageCell);
        }
        public void Set(in StorageCell storageCell, byte[] newValue)
        {
            DebugGuardInScope();
            _persistentStorageProvider.Set(storageCell, newValue);
        }
        public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        {
            DebugGuardInScope();
            return _transientStorageProvider.Get(storageCell);
        }
        public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        {
            DebugGuardInScope();
            _transientStorageProvider.Set(storageCell, newValue);
        }
        public void Reset(bool resetBlockChanges = true)
        {
            DebugGuardInScope();
            _stateProvider.Reset(resetBlockChanges);
            _persistentStorageProvider.Reset(resetBlockChanges);
            _transientStorageProvider.Reset(resetBlockChanges);
        }
        public void WarmUp(AccessList? accessList)
        {
            if (accessList?.IsEmpty == false)
            {
                foreach ((Address address, AccessList.StorageKeysEnumerable storages) in accessList)
                {
                    bool exists = _stateProvider.WarmUp(address);
                    foreach (UInt256 storage in storages)
                    {
                        _persistentStorageProvider.WarmUp(new StorageCell(address, in storage), isEmpty: !exists);
                    }
                }
            }
        }

        public void WarmUp(Address address) => _stateProvider.WarmUp(address);
        public void ClearStorage(Address address)
        {
            DebugGuardInScope();
            _persistentStorageProvider.ClearStorage(address);
            _transientStorageProvider.ClearStorage(address);
        }
        public void RecalculateStateRoot()
        {
            DebugGuardInScope();
            _stateProvider.RecalculateStateRoot();
        }
        public void DeleteAccount(Address address)
        {
            DebugGuardInScope();
            _stateProvider.DeleteAccount(address);
        }
        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            DebugGuardInScope();
            _stateProvider.CreateAccount(address, balance, nonce);
        }
        public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            DebugGuardInScope();
            return _stateProvider.InsertCode(address, codeHash, code, spec, isGenesis);
        }
        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            DebugGuardInScope();
            _stateProvider.AddToBalance(address, balanceChange, spec);
        }
        public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            DebugGuardInScope();
            return _stateProvider.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec);
        }
        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            DebugGuardInScope();
            _stateProvider.SubtractFromBalance(address, balanceChange, spec);
        }
        public void UpdateStorageRoot(Address address, Hash256 storageRoot)
        {
            DebugGuardInScope();
            _stateProvider.UpdateStorageRoot(address, storageRoot);
        }
        public void IncrementNonce(Address address, UInt256 delta)
        {
            DebugGuardInScope();
            _stateProvider.IncrementNonce(address, delta);
        }
        public void DecrementNonce(Address address, UInt256 delta)
        {
            DebugGuardInScope();
            _stateProvider.DecrementNonce(address, delta);
        }

        public void CommitTree(long blockNumber)
        {
            DebugGuardInScope();
            using (IBlockCommitter committer = _trieStore.BeginBlockCommit(blockNumber))
            {
                _persistentStorageProvider.CommitTrees(committer);
                _stateProvider.CommitTree();
            }
            _persistentStorageProvider.StateRoot = _stateProvider.StateRoot;
        }

        public UInt256 GetNonce(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.GetNonce(address);
        }

        public IDisposable BeginScope(BlockHeader? baseBlock)
        {
            if (_isInScope) throw new InvalidOperationException("Cannot create nested worldstate scope.");
            _isInScope = true;

            if (_logger.IsTrace) _logger.Trace($"Beginning WorldState scope with baseblock {baseBlock?.ToString(BlockHeader.Format.Short) ?? "null"} with stateroot {baseBlock?.StateRoot?.ToString() ?? "null"}.");

            StateRoot = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;
            IDisposable trieStoreCloser = _trieStore.BeginScope(baseBlock);

            return new Reactive.AnonymousDisposable(() =>
            {
                Reset();
                StateRoot = Keccak.EmptyTreeHash;
                trieStoreCloser.Dispose();
                _isInScope = false;
                if (_logger.IsTrace) _logger.Trace($"WorldState scope for baseblock {baseBlock?.ToString(BlockHeader.Format.Short) ?? "null"} closed");
            });
        }

        public bool IsInScope => _isInScope;

        public ref readonly UInt256 GetBalance(Address address)
        {
            DebugGuardInScope();
            return ref _stateProvider.GetBalance(address);
        }

        public ValueHash256 GetStorageRoot(Address address)
        {
            DebugGuardInScope();
            if (address == null) throw new ArgumentNullException(nameof(address));
            return _stateProvider.GetStorageRoot(address);
        }

        public byte[] GetCode(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.GetCode(address);
        }

        public byte[] GetCode(in ValueHash256 codeHash)
        {
            DebugGuardInScope();
            return _stateProvider.GetCode(in codeHash);
        }

        public ref readonly ValueHash256 GetCodeHash(Address address)
        {
            DebugGuardInScope();
            return ref _stateProvider.GetCodeHash(address);
        }

        ValueHash256 IAccountStateProvider.GetCodeHash(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.GetCodeHash(address);
        }

        public bool AccountExists(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.AccountExists(address);
        }
        public bool IsDeadAccount(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.IsDeadAccount(address);
        }

        public bool HasStateForBlock(BlockHeader? header)
        {
            return _trieStore.HasRoot(header?.StateRoot ?? Keccak.EmptyTreeHash);
        }

        public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
        {
            DebugGuardInScope();
            _persistentStorageProvider.Commit(commitRoots);
            _transientStorageProvider.Commit(commitRoots);
            _stateProvider.Commit(releaseSpec, commitRoots, isGenesis);
        }

        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        {
            DebugGuardInScope();
            _persistentStorageProvider.Commit(tracer, commitRoots);
            _transientStorageProvider.Commit(tracer, commitRoots);
            _stateProvider.Commit(releaseSpec, tracer, commitRoots, isGenesis);
        }

        public Snapshot TakeSnapshot(bool newTransactionStart = false)
        {
            DebugGuardInScope();
            int persistentSnapshot = _persistentStorageProvider.TakeSnapshot(newTransactionStart);
            int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
            Snapshot.Storage storageSnapshot = new Snapshot.Storage(persistentSnapshot, transientSnapshot);
            int stateSnapshot = _stateProvider.TakeSnapshot();
            return new Snapshot(storageSnapshot, stateSnapshot);
        }

        public void Restore(Snapshot snapshot)
        {
            DebugGuardInScope();
            _persistentStorageProvider.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);
            _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);
            _stateProvider.Restore(snapshot.StateSnapshot);
        }

        internal void Restore(int state, int persistentStorage, int transientStorage)
        {
            DebugGuardInScope();
            Restore(new Snapshot(new Snapshot.Storage(persistentStorage, transientStorage), state));
        }

        public void SetNonce(Address address, in UInt256 nonce)
        {
            DebugGuardInScope();
            _stateProvider.SetNonce(address, nonce);
        }

        public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            DebugGuardInScope();
            _stateProvider.CreateAccountIfNotExists(address, balance, nonce);
        }

        ArrayPoolList<AddressAsKey>? IWorldState.GetAccountChanges()
        {
            DebugGuardInScope();
            return _stateProvider.ChangedAddresses();
        }

        public void ResetTransient()
        {
            DebugGuardInScope();
            _transientStorageProvider.Reset();
        }

        PreBlockCaches? IPreBlockCaches.Caches => PreBlockCaches;
    }
}
