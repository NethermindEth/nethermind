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
    public class WorldState : IWorldState
    {
        internal readonly StateProvider _stateProvider;
        internal readonly PersistentStorageProvider _persistentStorageProvider;
        private readonly TransientStorageProvider _transientStorageProvider;
        private readonly ITrieStore _trieStore;
        private long _currentBlockNumber;
        private IReleaseSpec _currentSpec;
        private bool _isInitialized;
        public StateType StateType
        {
            get
            {
                return _currentBlockNumber >= VerkleTransitionBlock ? StateType.Verkle : StateType.Merkle;
            }
        }

        public Hash256 StateRoot
        {
            get => _stateProvider.StateRoot;
            set
            {
                _stateProvider.StateRoot = value;
                _persistentStorageProvider.StateRoot = value;
            }
        }

        // TODO: Should this be in release spec?
        private const long VerkleTransitionBlock = 24_000_000;

        // TODO: store verkle state tree in WorldState or store ref to VerkleWorldState?
        public WorldState(ITrieStore trieStore, IKeyValueStore? codeDb, ILogManager? logManager)
            : this(trieStore, codeDb, logManager, null, null)
        {
            _trieStore = trieStore;
            _stateProvider = new StateProvider(trieStore.GetTrieStore(null), codeDb, logManager);
            _persistentStorageProvider = new PersistentStorageProvider(trieStore, _stateProvider, logManager);
            _transientStorageProvider = new TransientStorageProvider(logManager);
        }

        internal WorldState(ITrieStore? trieStore, IKeyValueStore? codeDb, ILogManager? logManager, StateTree stateTree, IStorageTreeFactory storageTreeFactory)
        {
            _stateProvider = new StateProvider(trieStore.GetTrieStore(null), codeDb, logManager, stateTree);
            _persistentStorageProvider = new PersistentStorageProvider(trieStore, _stateProvider, logManager, storageTreeFactory);
            _transientStorageProvider = new TransientStorageProvider(logManager);
        }

        public void InitializeForBlock(long blockNumber, IReleaseSpec spec)
        {
            _currentBlockNumber = blockNumber;
            _currentSpec = spec;
            _isInitialized = true;
        }

        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("WorldState must be initialized with InitializeForBlock before use");
            }
        }

        public Account GetAccount(Address address)
        {
            EnsureInitialized();
            return _stateProvider.GetAccount(address);
        }

        bool IAccountStateProvider.TryGetAccount(Address address, out AccountStruct account)
        {
            EnsureInitialized();
            account = _stateProvider.GetAccount(address).ToStruct();
            return !account.IsTotallyEmpty;
        }

        public bool IsContract(Address address)
        {
            EnsureInitialized();
            return _stateProvider.IsContract(address);
        }

        public byte[] GetOriginal(in StorageCell storageCell)
        {
            EnsureInitialized();
            return _persistentStorageProvider.GetOriginal(storageCell);
        }
        public ReadOnlySpan<byte> Get(in StorageCell storageCell)
        {
            EnsureInitialized();
            return _persistentStorageProvider.Get(storageCell);
        }
        public void Set(in StorageCell storageCell, byte[] newValue)
        {
            EnsureInitialized();
            _persistentStorageProvider.Set(storageCell, newValue);
        }
        public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        {
            EnsureInitialized();
            return _transientStorageProvider.Get(storageCell);
        }
        public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        {
            EnsureInitialized();
            _transientStorageProvider.Set(storageCell, newValue);
        }
        public void Reset()
        {
            EnsureInitialized();
            _stateProvider.Reset();
            _persistentStorageProvider.Reset();
            _transientStorageProvider.Reset();
        }

        public void ClearStorage(Address address)
        {
            EnsureInitialized();
            _persistentStorageProvider.ClearStorage(address);
            _transientStorageProvider.ClearStorage(address);
        }
        public void RecalculateStateRoot()
        {
            EnsureInitialized();
            _stateProvider.RecalculateStateRoot();
        }
        public void DeleteAccount(Address address)
        {
            EnsureInitialized();
            _stateProvider.DeleteAccount(address);
        }
        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            EnsureInitialized();
            _stateProvider.CreateAccount(address, balance, nonce);
        }

        public void InsertCode(Address address, Hash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            EnsureInitialized();
            _stateProvider.InsertCode(address, codeHash, code, spec, isGenesis);
        }

        public void InsertCode(Address address, Hash256 codeHash, ReadOnlyMemory<byte> code, bool isGenesis = false)
        {
            EnsureInitialized();
            _stateProvider.InsertCode(address, codeHash, code, _currentSpec, isGenesis);
        }

        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            EnsureInitialized();
            _stateProvider.AddToBalance(address, balanceChange, spec);
        }
        public void AddToBalance(Address address, in UInt256 balanceChange)
        {
            EnsureInitialized();
            _stateProvider.AddToBalance(address, balanceChange, _currentSpec);
        }
        public void AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            EnsureInitialized();
            _stateProvider.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec);
        }
        public void AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange)
        {
            EnsureInitialized();
            _stateProvider.AddToBalanceAndCreateIfNotExists(address, balanceChange, _currentSpec);
        }
        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            EnsureInitialized();
            _stateProvider.SubtractFromBalance(address, balanceChange, spec);
        }
        public void SubtractFromBalance(Address address, in UInt256 balanceChange)
        {
            EnsureInitialized();
            _stateProvider.SubtractFromBalance(address, balanceChange, _currentSpec);
        }
        public void UpdateStorageRoot(Address address, Hash256 storageRoot)
        {
            EnsureInitialized();
            _stateProvider.UpdateStorageRoot(address, storageRoot);
        }
        public void IncrementNonce(Address address)
        {
            EnsureInitialized();
            _stateProvider.IncrementNonce(address);
        }
        public void DecrementNonce(Address address)
        {
            EnsureInitialized();
            _stateProvider.DecrementNonce(address);
        }

        public void CommitTree(long blockNumber)
        {
            EnsureInitialized();
            _persistentStorageProvider.CommitTrees(blockNumber);
            _stateProvider.CommitTree(blockNumber);
            _persistentStorageProvider.StateRoot = _stateProvider.StateRoot;
        }

        public void TouchCode(in ValueHash256 codeHash)
        {
            EnsureInitialized();
            _stateProvider.TouchCode(codeHash);
        }

        public UInt256 GetNonce(Address address)
        {
            EnsureInitialized();
            return _stateProvider.GetNonce(address);
        }

        public UInt256 GetBalance(Address address)
        {
            EnsureInitialized();
            return _stateProvider.GetBalance(address);
        }

        public ValueHash256 GetStorageRoot(Address address)
        {
            EnsureInitialized();
            return _stateProvider.GetStorageRoot(address);
        }

        public byte[] GetCode(Address address)
        {
            EnsureInitialized();
            return _stateProvider.GetCode(address);
        }

        public byte[] GetCode(Hash256 codeHash)
        {
            EnsureInitialized();
            return _stateProvider.GetCode(codeHash);
        }

        public byte[] GetCode(ValueHash256 codeHash)
        {
            EnsureInitialized();
            return _stateProvider.GetCode(codeHash);
        }

        public Hash256 GetCodeHash(Address address)
        {
            EnsureInitialized();
            return _stateProvider.GetCodeHash(address);
        }

        ValueHash256 IAccountStateProvider.GetCodeHash(Address address)
        {
            EnsureInitialized();
            return _stateProvider.GetCodeHash(address);
        }

        public void Accept(ITreeVisitor visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null)
        {
            EnsureInitialized();
            _stateProvider.Accept(visitor, stateRoot, visitingOptions);
        }
        public bool AccountExists(Address address)
        {
            EnsureInitialized();
            return _stateProvider.AccountExists(address);
        }
        public bool IsDeadAccount(Address address)
        {
            EnsureInitialized();
            return _stateProvider.IsDeadAccount(address);
        }
        public bool IsEmptyAccount(Address address)
        {
            EnsureInitialized();
            return _stateProvider.IsEmptyAccount(address);
        }

        public bool HasStateForRoot(Hash256 stateRoot)
        {
            EnsureInitialized();
            return _trieStore.HasRoot(stateRoot);
        }

        public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false)
        {
            EnsureInitialized();
            _persistentStorageProvider.Commit(commitStorageRoots);
            _transientStorageProvider.Commit(commitStorageRoots);
            _stateProvider.Commit(releaseSpec, isGenesis);
        }
        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false)
        {
            EnsureInitialized();
            _persistentStorageProvider.Commit(tracer);
            _transientStorageProvider.Commit(tracer);
            _stateProvider.Commit(releaseSpec, tracer, isGenesis);
        }

        public Snapshot TakeSnapshot(bool newTransactionStart = false)
        {
            EnsureInitialized();
            int persistentSnapshot = _persistentStorageProvider.TakeSnapshot(newTransactionStart);
            int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
            Snapshot.Storage storageSnapshot = new Snapshot.Storage(persistentSnapshot, transientSnapshot);
            int stateSnapshot = _stateProvider.TakeSnapshot();
            return new Snapshot(stateSnapshot, storageSnapshot);
        }

        public void Restore(Snapshot snapshot)
        {
            EnsureInitialized();
            _persistentStorageProvider.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);
            _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);
            _stateProvider.Restore(snapshot.StateSnapshot);
        }

        internal void Restore(int state, int persistantStorage, int transientStorage)
        {
            EnsureInitialized();
            Restore(new Snapshot(state, new Snapshot.Storage(persistantStorage, transientStorage)));
        }

        internal void SetNonce(Address address, in UInt256 nonce)
        {
            EnsureInitialized();
            _stateProvider.SetNonce(address, nonce);
        }

        public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            EnsureInitialized();
            _stateProvider.CreateAccountIfNotExists(address, balance, nonce);
        }

        public byte[] GetCodeChunk(Address codeOwner, UInt256 chunkId) => throw new NotImplementedException();
    }
}
