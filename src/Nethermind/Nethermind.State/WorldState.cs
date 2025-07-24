// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        private PreBlockCaches? PreBlockCaches { get; }

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
            _trieStore = trieStore;
            _stateProvider = new StateProvider(trieStore.GetTrieStore(null), codeDb, logManager, stateTree, PreBlockCaches?.StateCache, populatePreBlockCache);
            _persistentStorageProvider = new PersistentStorageProvider(trieStore, _stateProvider, logManager, storageTreeFactory, PreBlockCaches?.StorageCache, populatePreBlockCache);
            _transientStorageProvider = new TransientStorageProvider(logManager);
        }

        public WorldState(ITrieStore trieStore, IKeyValueStoreWithBatching? codeDb, ILogManager? logManager, PreBlockCaches? preBlockCaches, bool populatePreBlockCache = true)
            : this(trieStore, codeDb, logManager, null, preBlockCaches: preBlockCaches, populatePreBlockCache: populatePreBlockCache)
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GuardInScope()
        {
            if (!_isInScope) throw new InvalidOperationException($"{nameof(IWorldState)} must only be used within scope");
        }

        public Account GetAccount(Address address)
        {
            GuardInScope();
            return _stateProvider.GetAccount(address);
        }

        bool IAccountStateProvider.TryGetAccount(Address address, out AccountStruct account)
        {
            account = _stateProvider.GetAccount(address).ToStruct();
            return !account.IsTotallyEmpty;
        }

        public bool IsContract(Address address)
        {
            GuardInScope();
            return _stateProvider.IsContract(address);
        }

        public byte[] GetOriginal(in StorageCell storageCell)
        {
            GuardInScope();
            return _persistentStorageProvider.GetOriginal(storageCell);
        }
        public ReadOnlySpan<byte> Get(in StorageCell storageCell)
        {
            GuardInScope();
            return _persistentStorageProvider.Get(storageCell);
        }
        public void Set(in StorageCell storageCell, byte[] newValue)
        {
            GuardInScope();
            _persistentStorageProvider.Set(storageCell, newValue);
        }
        public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        {
            GuardInScope();
            return _transientStorageProvider.Get(storageCell);
        }
        public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        {
            GuardInScope();
            _transientStorageProvider.Set(storageCell, newValue);
        }
        public void Reset(bool resetBlockChanges = true)
        {
            GuardInScope();
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
            GuardInScope();
            _persistentStorageProvider.ClearStorage(address);
            _transientStorageProvider.ClearStorage(address);
        }
        public void RecalculateStateRoot()
        {
            GuardInScope();
            _stateProvider.RecalculateStateRoot();
        }
        public void DeleteAccount(Address address)
        {
            GuardInScope();
            _stateProvider.DeleteAccount(address);
        }
        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            GuardInScope();
            _stateProvider.CreateAccount(address, balance, nonce);
        }
        public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            GuardInScope();
            return _stateProvider.InsertCode(address, codeHash, code, spec, isGenesis);
        }
        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            GuardInScope();
            _stateProvider.AddToBalance(address, balanceChange, spec);
        }
        public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            GuardInScope();
            return _stateProvider.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec);
        }
        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            GuardInScope();
            _stateProvider.SubtractFromBalance(address, balanceChange, spec);
        }
        public void UpdateStorageRoot(Address address, Hash256 storageRoot)
        {
            GuardInScope();
            _stateProvider.UpdateStorageRoot(address, storageRoot);
        }
        public void IncrementNonce(Address address, UInt256 delta)
        {
            GuardInScope();
            _stateProvider.IncrementNonce(address, delta);
        }
        public void DecrementNonce(Address address, UInt256 delta)
        {
            GuardInScope();
            _stateProvider.DecrementNonce(address, delta);
        }

        public void CommitTree(long blockNumber)
        {
            GuardInScope();
            using (IBlockCommitter committer = _trieStore.BeginBlockCommit(blockNumber))
            {
                _persistentStorageProvider.CommitTrees(committer);
                _stateProvider.CommitTree();
            }
            _persistentStorageProvider.StateRoot = _stateProvider.StateRoot;
        }

        public UInt256 GetNonce(Address address)
        {
            GuardInScope();
            return _stateProvider.GetNonce(address);
        }

        public IDisposable BeginScope(BlockHeader? baseBlock)
        {
            if (_isInScope) throw new InvalidOperationException("Cannot create nested worldstate scope.");
            _isInScope = true;
            StateRoot = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;

            return new Reactive.AnonymousDisposable(() =>
            {
                Reset();
                StateRoot = Keccak.EmptyTreeHash;
                _isInScope = false;
            });
        }

        public bool IsInScope => _isInScope;

        public ref readonly UInt256 GetBalance(Address address)
        {
            GuardInScope();
            return ref _stateProvider.GetBalance(address);
        }

        UInt256 IAccountStateProvider.GetBalance(Address address)
        {
            GuardInScope();
            return _stateProvider.GetBalance(address);
        }

        public ValueHash256 GetStorageRoot(Address address)
        {
            GuardInScope();
            if (address == null) throw new ArgumentNullException(nameof(address));
            return _stateProvider.GetStorageRoot(address);
        }

        public byte[] GetCode(Address address)
        {
            GuardInScope();
            return _stateProvider.GetCode(address);
        }

        public byte[] GetCode(in ValueHash256 codeHash)
        {
            GuardInScope();
            return _stateProvider.GetCode(in codeHash);
        }

        public ref readonly ValueHash256 GetCodeHash(Address address)
        {
            GuardInScope();
            return ref _stateProvider.GetCodeHash(address);
        }

        ValueHash256 IAccountStateProvider.GetCodeHash(Address address)
        {
            GuardInScope();
            return _stateProvider.GetCodeHash(address);
        }

        public bool AccountExists(Address address)
        {
            GuardInScope();
            return _stateProvider.AccountExists(address);
        }
        public bool IsDeadAccount(Address address)
        {
            GuardInScope();
            return _stateProvider.IsDeadAccount(address);
        }

        public bool HasStateForBlock(BlockHeader? header)
        {
            return _trieStore.HasRoot(header?.StateRoot ?? Keccak.EmptyTreeHash);
        }

        public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
        {
            GuardInScope();
            _persistentStorageProvider.Commit(commitRoots);
            _transientStorageProvider.Commit(commitRoots);
            _stateProvider.Commit(releaseSpec, commitRoots, isGenesis);
        }

        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        {
            GuardInScope();
            _persistentStorageProvider.Commit(tracer, commitRoots);
            _transientStorageProvider.Commit(tracer, commitRoots);
            _stateProvider.Commit(releaseSpec, tracer, commitRoots, isGenesis);
        }

        public Snapshot TakeSnapshot(bool newTransactionStart = false)
        {
            GuardInScope();
            int persistentSnapshot = _persistentStorageProvider.TakeSnapshot(newTransactionStart);
            int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
            Snapshot.Storage storageSnapshot = new Snapshot.Storage(persistentSnapshot, transientSnapshot);
            int stateSnapshot = _stateProvider.TakeSnapshot();
            return new Snapshot(storageSnapshot, stateSnapshot);
        }

        public void Restore(Snapshot snapshot)
        {
            GuardInScope();
            _persistentStorageProvider.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);
            _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);
            _stateProvider.Restore(snapshot.StateSnapshot);
        }

        internal void Restore(int state, int persistentStorage, int transientStorage)
        {
            GuardInScope();
            Restore(new Snapshot(new Snapshot.Storage(persistentStorage, transientStorage), state));
        }

        public void SetNonce(Address address, in UInt256 nonce)
        {
            GuardInScope();
            _stateProvider.SetNonce(address, nonce);
        }

        public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            GuardInScope();
            _stateProvider.CreateAccountIfNotExists(address, balance, nonce);
        }

        ArrayPoolList<AddressAsKey>? IWorldState.GetAccountChanges()
        {
            GuardInScope();
            return _stateProvider.ChangedAddresses();
        }

        public void ResetTransient()
        {
            GuardInScope();
            _transientStorageProvider.Reset();
        }

        PreBlockCaches? IPreBlockCaches.Caches => PreBlockCaches;
    }
}
