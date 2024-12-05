// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Tracing;
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
    public class WorldState : IWorldState, IPreBlockCaches, IStateOwner
    {
        internal readonly StateProvider _stateProvider;
        internal readonly PersistentStorageProvider _persistentStorageProvider;
        private readonly TransientStorageProvider _transientStorageProvider;
        private readonly IStateFactory _factory;
        private readonly bool _prefetchMerkle;
        private IState? _state;
        private Hash256 _stateRoot;
        private PreBlockCaches? PreBlockCaches { get; }

        public Hash256 StateRoot
        {
            get => _state is null ? _stateRoot : _state.StateRoot;
            set
            {
                ResetState(value);
            }
        }

        public WorldState(IStateFactory factory, IKeyValueStore? codeDb, ILogManager? logManager)
            : this(factory, codeDb, logManager, null)
        {
        }

        public WorldState(
            IStateFactory factory,
            IKeyValueStore? codeDb,
            ILogManager? logManager,
            PreBlockCaches? preBlockCaches = null,
            bool populatePreBlockCache = true,
            bool prefetchMerkle = false)
        {
            _factory = factory;
            _prefetchMerkle = prefetchMerkle;
            PreBlockCaches = preBlockCaches;
            _stateProvider = new StateProvider(this, factory, codeDb, logManager, PreBlockCaches?.StateCache, populatePreBlockCache);
            _state = _factory.Get(Keccak.EmptyTreeHash, false);
            _stateRoot = Keccak.EmptyTreeHash;
            _persistentStorageProvider = new PersistentStorageProvider(this, logManager, PreBlockCaches?.StorageCache,
                populatePreBlockCache);
            _transientStorageProvider = new TransientStorageProvider(logManager);
        }

        public Account GetAccount(Address address)
        {
            return _stateProvider.GetAccount(address);
        }

        bool IAccountStateProvider.TryGetAccount(Address address, out AccountStruct account)
        {
            account = _stateProvider.GetAccount(address).ToStruct();
            return !account.IsTotallyEmpty;
        }

        public bool IsContract(Address address)
        {
            return _stateProvider.IsContract(address);
        }

        public byte[] GetOriginal(in StorageCell storageCell)
        {
            return _persistentStorageProvider.GetOriginal(storageCell);
        }
        public ReadOnlySpan<byte> Get(in StorageCell storageCell)
        {
            return _persistentStorageProvider.Get(storageCell);
        }
        public void Set(in StorageCell storageCell, byte[] newValue)
        {
            _persistentStorageProvider.Set(storageCell, newValue);
        }
        public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        {
            return _transientStorageProvider.Get(storageCell);
        }
        public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        {
            _transientStorageProvider.Set(storageCell, newValue);
        }
        public void Reset(bool resizeCollections)
        {
            _stateProvider.Reset(resizeCollections);
            _persistentStorageProvider.Reset(resizeCollections);
            _transientStorageProvider.Reset(resizeCollections);
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
                        _persistentStorageProvider.WarmUp(new StorageCell(address, storage), isEmpty: !exists);
                    }
                }
            }
        }

        public void WarmUp(Address address) => _stateProvider.WarmUp(address);

        public void Reset()
        {
            _stateProvider.Reset();
            _persistentStorageProvider.Reset();
            _transientStorageProvider.Reset();
        }

        public void FullReset()
        {
            ResetStateToNull();
            Reset();
        }

        public void ResetTo(Hash256 stateRoot)
        {
        	stateRoot ??= _stateRoot;
            ResetState(stateRoot);
            _stateProvider.Reset();
            _persistentStorageProvider.Reset();
            _transientStorageProvider.Reset();
        }

        public void ClearStorage(Address address)
        {
            _persistentStorageProvider.ClearStorage(address);
            _transientStorageProvider.ClearStorage(address);
        }

        public void DeleteAccount(Address address)
        {
            _stateProvider.DeleteAccount(address);
        }
        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            _stateProvider.CreateAccount(address, balance, nonce);
        }
        public void InsertCode(Address address, Hash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            _stateProvider.InsertCode(address, codeHash, code, spec, isGenesis);
        }
        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            _stateProvider.AddToBalance(address, balanceChange, spec);
        }
        public void AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            _stateProvider.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec);
        }
        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            _stateProvider.SubtractFromBalance(address, balanceChange, spec);
        }
        public void UpdateStorageRoot(Address address, Hash256 storageRoot)
        {
            _stateProvider.UpdateStorageRoot(address, storageRoot);
        }
        public void IncrementNonce(Address address, UInt256 delta)
        {
            _stateProvider.IncrementNonce(address, delta);
        }
        public void DecrementNonce(Address address, UInt256 delta)
        {
            _stateProvider.DecrementNonce(address, delta);
        }

        public void CommitTree(long blockNumber)
        {
            _state.Commit(blockNumber);
            _stateRoot = _state.StateRoot;
            ResetState(_state.StateRoot);
        }

        public UInt256 GetNonce(Address address) => _stateProvider.GetNonce(address);

        public UInt256 GetBalance(Address address) => _stateProvider.GetBalance(address);

        public ValueHash256 GetStorageRoot(Address address) => _stateProvider.GetStorageRoot(address);

        public byte[] GetCode(Address address) => _stateProvider.GetCode(address);

        public byte[] GetCode(Hash256 codeHash) => _stateProvider.GetCode(codeHash);

        public byte[] GetCode(ValueHash256 codeHash) => _stateProvider.GetCode(codeHash);

        public Hash256 GetCodeHash(Address address) => _stateProvider.GetCodeHash(address);

        ValueHash256 IAccountStateProvider.GetCodeHash(Address address)
        {
            return _stateProvider.GetCodeHash(address);
        }

        public void Accept(ITreeVisitor visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null)
        {
            _stateProvider.Accept(visitor, stateRoot, visitingOptions);
        }
        public bool AccountExists(Address address)
        {
            return _stateProvider.AccountExists(address);
        }
        public bool IsDeadAccount(Address address)
        {
            return _stateProvider.IsDeadAccount(address);
        }
        public bool IsEmptyAccount(Address address)
        {
            return _stateProvider.IsEmptyAccount(address);
        }

        public bool HasStateForRoot(Hash256 stateRoot)
        {
            return _factory.HasRoot(stateRoot);
        }

        public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitStorageRoots = true)
        {
            _persistentStorageProvider.Commit();
            _transientStorageProvider.Commit();
            _stateProvider.Commit(releaseSpec, isGenesis);
        }
        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitStorageRoots = true)
        {
            _persistentStorageProvider.Commit(tracer);
            _transientStorageProvider.Commit(tracer);
            _stateProvider.Commit(releaseSpec, tracer, isGenesis);
        }

        public Snapshot TakeSnapshot(bool newTransactionStart = false)
        {
            int persistentSnapshot = _persistentStorageProvider.TakeSnapshot(newTransactionStart);
            int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
            Snapshot.Storage storageSnapshot = new Snapshot.Storage(persistentSnapshot, transientSnapshot);
            int stateSnapshot = _stateProvider.TakeSnapshot();
            return new Snapshot(stateSnapshot, storageSnapshot);
        }

        public void Restore(Snapshot snapshot)
        {
            _persistentStorageProvider.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);
            _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);
            _stateProvider.Restore(snapshot.StateSnapshot);
        }

        internal void Restore(int state, int persistantStorage, int transientStorage)
        {
            Restore(new Snapshot(state, new Snapshot.Storage(persistantStorage, transientStorage)));
        }

        // Needed for benchmarks
        internal void SetNonce(Address address, in UInt256 nonce)
        {
            _stateProvider.SetNonce(address, nonce);
        }

        public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            _stateProvider.CreateAccountIfNotExists(address, balance, nonce);
        }

        private void ResetState(Hash256 stateRoot)
        {
            Interlocked.Exchange(ref _state, _factory.Get(stateRoot, _prefetchMerkle))?.Dispose();
        }

        private void ResetStateToNull()
        {
            Interlocked.Exchange(ref _state, null)?.Dispose();
        }

        ArrayPoolList<AddressAsKey>? IWorldState.GetAccountChanges() => _stateProvider.ChangedAddresses();

        PreBlockCaches? IPreBlockCaches.Caches => PreBlockCaches;
        public IState State => _state;
    }
}
