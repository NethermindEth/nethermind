// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Tracing;
using Nethermind.Trie;
using EvmWord = System.Runtime.Intrinsics.Vector256<byte>;

[assembly: InternalsVisibleTo("Ethereum.Test.Base")]
[assembly: InternalsVisibleTo("Ethereum.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.State.Test")]
[assembly: InternalsVisibleTo("Nethermind.Benchmark")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization")]

namespace Nethermind.State
{
    public class WorldState : IWorldState, IStateOwner
    {
        private readonly IStateFactory _factory;
        private readonly StateProvider _stateProvider;
        private readonly PersistentStorageProvider _persistentStorageProvider;
        private readonly TransientStorageProvider _transientStorageProvider;
        private IState _state;

        public Hash256 StateRoot
        {
            get => _state.StateRoot;
            set
            {
                // clean previous and get new
                _state.Dispose();
                _state = _factory.Get(value);
            }
        }

        public IState State => _state!;

        public WorldState(IStateFactory factory, IKeyValueStore? codeDb, ILogManager? logManager)
        {
            _factory = factory;
            _stateProvider = new StateProvider(this, factory, codeDb, logManager);
            _state = _factory.Get(Keccak.EmptyTreeHash);
            _persistentStorageProvider = new PersistentStorageProvider(this, logManager);
            _transientStorageProvider = new TransientStorageProvider(logManager);
        }

        public AccountStruct GetAccount(Address address)
        {
            return _stateProvider.GetAccount(address);
        }

        bool IAccountStateProvider.TryGetAccount(Address address, out AccountStruct account)
        {
            account = _stateProvider.GetAccount(address);
            return !account.IsTotallyEmpty;
        }

        public bool IsContract(Address address)
        {
            return _stateProvider.IsContract(address);
        }

        public EvmWord GetOriginal(in StorageCell storageCell)
        {
            return _persistentStorageProvider.GetOriginal(storageCell);
        }
        public EvmWord Get(in StorageCell storageCell)
        {
            return _persistentStorageProvider.Get(storageCell);
        }
        public void Set(in StorageCell storageCell, EvmWord newValue)
        {
            _persistentStorageProvider.Set(storageCell, newValue);
        }
        public EvmWord GetTransientState(in StorageCell storageCell)
        {
            return _transientStorageProvider.Get(storageCell);
        }
        public void SetTransientState(in StorageCell storageCell, EvmWord newValue)
        {
            _transientStorageProvider.Set(storageCell, newValue);
        }
        public void Reset()
        {
            _stateProvider.Reset();
            _persistentStorageProvider.Reset();
            _transientStorageProvider.Reset();
        }
        public void FullReset()
        {
            _state.Reset();
            _stateProvider.Reset();
            _persistentStorageProvider.Reset();
            _transientStorageProvider.Reset();
        }

        public void ResetTo(Hash256 stateRoot)
        {
            _state.Dispose();
            _state = _factory.Get(stateRoot);
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
        public void IncrementNonce(Address address)
        {
            _stateProvider.IncrementNonce(address);
        }
        public void DecrementNonce(Address address)
        {
            _stateProvider.DecrementNonce(address);
        }

        public void CommitTree(long blockNumber)
        {
            _state.Commit(blockNumber);

            // clean previous and get new
            IState previous = _state;
            previous.Dispose();
            _state = _factory.Get(previous.StateRoot);
        }

        public void TouchCode(in ValueHash256 codeHash)
        {
            _stateProvider.TouchCode(codeHash);
        }

        public UInt256 GetNonce(Address address) => _stateProvider.GetNonce(address);

        public UInt256 GetBalance(Address address) => _stateProvider.GetBalance(address);

        public ValueHash256 GetStorageRoot(Address address) => _stateProvider.GetStorageRoot(address);

        public byte[] GetCode(Address address) => _stateProvider.GetCode(address);

        public byte[] GetCode(Hash256 codeHash) => _stateProvider.GetCode(codeHash);

        public byte[] GetCode(ValueHash256 codeHash) => _stateProvider.GetCode(codeHash);

        public ValueHash256 GetCodeHash(Address address) => _stateProvider.GetCodeHash(address);

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

        public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false)
        {
            _persistentStorageProvider.Commit();
            _transientStorageProvider.Commit();
            _stateProvider.Commit(releaseSpec, isGenesis);
        }
        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false)
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

        internal void SetNonce(Address address, in UInt256 nonce)
        {
            _stateProvider.SetNonce(address, nonce);
        }

        public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            _stateProvider.CreateAccountIfNotExists(address, balance, nonce);
        }
    }
}
