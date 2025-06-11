// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
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
    public class WorldState : IWorldState, IPreBlockCaches
    {
        internal readonly StateProvider _stateProvider;
        internal readonly PersistentStorageProvider _persistentStorageProvider;
        private readonly TransientStorageProvider _transientStorageProvider;
        private readonly ITrieStore _trieStore;
        private readonly IKeyValueStore _codeDb;
        private PreBlockCaches? PreBlockCaches { get; }

        public Hash256 StateRoot
        {
            get => _stateProvider.StateRoot;
            set
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
            _codeDb = codeDb;
        }

        public WorldState(ITrieStore trieStore, IKeyValueStoreWithBatching? codeDb, ILogManager? logManager, PreBlockCaches? preBlockCaches, bool populatePreBlockCache = true)
            : this(trieStore, codeDb, logManager, null, preBlockCaches: preBlockCaches, populatePreBlockCache: populatePreBlockCache)
        {
        }

        public Account GetAccount(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.GetAccount(address);
        }

        bool IAccountStateProvider.TryGetAccount(Address address, out AccountStruct account)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            account = _stateProvider.GetAccount(address).ToStruct();
            return !account.IsTotallyEmpty;
        }

        public bool IsContract(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.IsContract(address);
        }

        public StateType StateType => StateType.Merkle;

        public byte[] GetOriginal(in StorageCell storageCell)
        {
            Span<byte> key = stackalloc byte[32];
            StorageTree.ComputeKey(storageCell.Index, key);
            _codeDb[key] = storageCell.Index.ToBigEndian();
            return _persistentStorageProvider.GetOriginal(storageCell);
        }
        public ReadOnlySpan<byte> Get(in StorageCell storageCell)
        {
            Span<byte> key = stackalloc byte[32];
            StorageTree.ComputeKey(storageCell.Index, key);
            _codeDb[key] = storageCell.Index.ToBigEndian();
            return _persistentStorageProvider.Get(storageCell);
        }
        public void Set(in StorageCell storageCell, byte[] newValue)
        {
            Span<byte> key = stackalloc byte[32];
            StorageTree.ComputeKey(storageCell.Index, key);
            _codeDb[key] = storageCell.Index.ToBigEndian();
            _persistentStorageProvider.Set(storageCell, newValue);
        }
        public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        {
            Span<byte> key = stackalloc byte[32];
            StorageTree.ComputeKey(storageCell.Index, key);
            _codeDb[key] = storageCell.Index.ToBigEndian();
            return _transientStorageProvider.Get(storageCell);
        }
        public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        {
            Span<byte> key = stackalloc byte[32];
            StorageTree.ComputeKey(storageCell.Index, key);
            _codeDb[key] = storageCell.Index.ToBigEndian();
            _transientStorageProvider.Set(storageCell, newValue);
        }
        public void Reset(bool resetBlockChanges = true)
        {
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
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            _persistentStorageProvider.ClearStorage(address);
            _transientStorageProvider.ClearStorage(address);
        }
        public void RecalculateStateRoot()
        {
            _stateProvider.RecalculateStateRoot();
        }
        public void DeleteAccount(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            _stateProvider.DeleteAccount(address);
        }
        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            _stateProvider.CreateAccount(address, balance, nonce);
        }
        public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.InsertCode(address, codeHash, code, spec, isGenesis);
        }
        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            _stateProvider.AddToBalance(address, balanceChange, spec);
        }
        public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec);
        }
        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            _stateProvider.SubtractFromBalance(address, balanceChange, spec);
        }
        public void UpdateStorageRoot(Address address, Hash256 storageRoot)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            _stateProvider.UpdateStorageRoot(address, storageRoot);
        }
        public void IncrementNonce(Address address, UInt256 delta)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            _stateProvider.IncrementNonce(address, delta);
        }
        public void DecrementNonce(Address address, UInt256 delta)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            _stateProvider.DecrementNonce(address, delta);
        }

        public void CommitTree(long blockNumber)
        {
            using (IBlockCommitter committer = _trieStore.BeginBlockCommit(blockNumber))
            {
                _persistentStorageProvider.CommitTrees(committer);
                _stateProvider.CommitTree();
            }
            _persistentStorageProvider.StateRoot = _stateProvider.StateRoot;
        }

        public UInt256 GetNonce(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.GetNonce(address);
        }

        public UInt256 GetBalance(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.GetBalance(address);
        }

        public ValueHash256 GetStorageRoot(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.GetStorageRoot(address);
        }

        public byte[] GetCode(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.GetCode(address);
        }

        public byte[] GetCode(Hash256 codeHash) => _stateProvider.GetCode(codeHash);

        public byte[] GetCode(ValueHash256 codeHash) => _stateProvider.GetCode(codeHash);

        public Hash256 GetCodeHash(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.GetCodeHash(address);
        }

        ValueHash256 IAccountStateProvider.GetCodeHash(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.GetCodeHash(address);
        }

        public void Accept<TContext>(ITreeVisitor<TContext> visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TContext : struct, INodeContext<TContext>
        {
            _stateProvider.Accept(visitor, stateRoot, visitingOptions);
        }

        public bool AccountExists(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.AccountExists(address);
        }
        public bool IsDeadAccount(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.IsDeadAccount(address);
        }
        public bool IsEmptyAccount(Address address)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            return _stateProvider.IsEmptyAccount(address);
        }

        public bool HasStateForRoot(Hash256 stateRoot)
        {
            return _trieStore.HasRoot(stateRoot);
        }

        public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
        {
            _persistentStorageProvider.Commit(commitRoots);
            _transientStorageProvider.Commit(commitRoots);
            _stateProvider.Commit(releaseSpec, commitRoots, isGenesis);
        }
        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        {
            _persistentStorageProvider.Commit(tracer, commitRoots);
            _transientStorageProvider.Commit(tracer, commitRoots);
            _stateProvider.Commit(releaseSpec, tracer, commitRoots, isGenesis);
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

        internal void Restore(int state, int persistentStorage, int transientStorage)
        {
            Restore(new Snapshot(state, new Snapshot.Storage(persistentStorage, transientStorage)));
        }

        public void SetNonce(Address address, in UInt256 nonce)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            _stateProvider.SetNonce(address, nonce);
        }

        public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            _codeDb[ValueKeccak.Compute(address.Bytes).BytesAsSpan] = address.Bytes;
            _stateProvider.CreateAccountIfNotExists(address, balance, nonce);
        }

        ArrayPoolList<AddressAsKey>? IWorldState.GetAccountChanges() => _stateProvider.ChangedAddresses();
        public void ResetTransient()
        {
            _transientStorageProvider.Reset();
        }

        public byte[] GetCodeChunk(Address codeOwner, UInt256 chunkId)
        {
            throw new NotImplementedException();
        }

        public void SweepLeaves(int blockNumber)
        {
        }

        public bool ValuePresentInTree(Hash256 key)
        {
            throw new NotImplementedException();
        }

        PreBlockCaches? IPreBlockCaches.Caches => PreBlockCaches;
    }
}
