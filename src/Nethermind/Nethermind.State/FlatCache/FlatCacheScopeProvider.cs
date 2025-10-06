// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NonBlocking;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace Nethermind.State.FlatCache;

public readonly record struct StateId(long blockNumber, ValueHash256 stateRoot) : IComparable<StateId>
{
    public int CompareTo(StateId other)
    {
        var blockNumberComparison = blockNumber.CompareTo(other.blockNumber);
        if (blockNumberComparison != 0) return blockNumberComparison;
        return stateRoot.CompareTo(other.stateRoot);
    }
}

public sealed class FlatCacheScopeProvider : IWorldStateScopeProvider, IPreBlockCaches
{
    private static Gauge _gatheredSize = Metrics.CreateGauge("flatcache_known_states_size", "size");

    private readonly IWorldStateScopeProvider _baseScopeProvider;
    private readonly FlatCacheRepository _repository;
    private readonly bool _isReadOnly;
    private ILogger _logger;

    public FlatCacheScopeProvider(IWorldStateScopeProvider baseScopeProvider, FlatCacheRepository repository, bool isReadOnly, ILogManager logManager)
    {
        _baseScopeProvider = baseScopeProvider;
        _repository = repository;
        _isReadOnly = isReadOnly;
        _logger = logManager.GetClassLogger<FlatCacheScopeProvider>();
    }

    public bool HasRoot(BlockHeader? baseBlock) => _baseScopeProvider.HasRoot(baseBlock);


    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        StateId baseBlockId = new StateId(baseBlock?.Number ?? -1, baseBlock?.StateRoot ?? Keccak.EmptyTreeHash);

        SnapshotBundle gatheredCache = _repository.GatherCache(baseBlockId);
        _gatheredSize.Set(gatheredCache.SnapshotCount);

        return new ScopeWrapper(_baseScopeProvider.BeginScope(baseBlock), this, gatheredCache, baseBlockId);
    }

    private sealed class ScopeWrapper(IWorldStateScopeProvider.IScope baseScope, FlatCacheScopeProvider flatCache, SnapshotBundle snapshotBundle, StateId baseBlock) : IWorldStateScopeProvider.IScope
    {
        private StateId _currentBaseBlock = baseBlock;

        private static Prometheus.Counter _cacheHit =
            Prometheus.Metrics.CreateCounter("flatcache_state_tree_cachehit", "hit rate", "cachehit");

        private static Counter.Child _cacheHitHit = _cacheHit.WithLabels("hit");
        private static Counter.Child _cacheHitMiss = _cacheHit.WithLabels("miss");
        private Dictionary<Address, StorageTreeWrapper> _loadedStorages = new Dictionary<Address, StorageTreeWrapper>();

        public Hash256 RootHash => baseScope.RootHash;

        public Account? Get(Address address)
        {
            if (snapshotBundle.TryGetAccount(address, out var account))
            {
                _cacheHitHit.Inc();
                return account;
            }
            _cacheHitMiss.Inc();
            return baseScope.Get(address);
        }

        public void HintAccountRead(Address address, Account? account)
        {
            baseScope.HintAccountRead(address, account);
            snapshotBundle.SetChangedAccount(address, account);
        }

        public void UpdateRootHash()
        {
            baseScope.UpdateRootHash();
        }


        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            // Note: baseScope.CreateStorageTree(address) cannot be lazy. I dont know why.
            // TODO: Double check again. Maybe it just need the storage root correct.

            ref StorageTreeWrapper wrapper = ref CollectionsMarshal.GetValueRefOrAddDefault(_loadedStorages, address, out var exists);
            if (!exists)
            {
                wrapper = new StorageTreeWrapper(baseScope.CreateStorageTree(address), snapshotBundle.GatherStorageCache(address));
            }

            return wrapper;
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
            return new WriteBatchWrapper(baseScope.StartWriteBatch(estimatedAccountNum), snapshotBundle);
        }

        public void Commit(long blockNumber)
        {
            baseScope.Commit(blockNumber);
            Snapshot snapshot = snapshotBundle.CollectAndApplyKnownState();
            var newId = new StateId(blockNumber, RootHash);

            if (!flatCache._isReadOnly)
            {
                snapshot = snapshot with
                {
                    From = _currentBaseBlock,
                    To = newId
                };
                flatCache._repository.RegisterKnownState(_currentBaseBlock, newId, snapshot);
            }

            _currentBaseBlock = newId;
        }

        public void Dispose()
        {
            baseScope.Dispose();
            snapshotBundle.Dispose();
        }
    }

    private sealed class StorageTreeWrapper(
        IWorldStateScopeProvider.IStorageTree baseStorageTree,
        StorageSnapshotBundle cache) : IWorldStateScopeProvider.IStorageTree
    {
        private static Counter _cacheHit = Metrics.CreateCounter("flatcache_storage_tree_cachehit", "hit rate", "cachehit");
        private static Counter.Child _cacheHitHit = _cacheHit.WithLabels("hit");
        private static Counter.Child _cacheHitMiss = _cacheHit.WithLabels("miss");

        public Hash256 RootHash => baseStorageTree.RootHash;

        [MethodImpl(MethodImplOptions.NoOptimization)]
        public byte[] Get(in UInt256 index)
        {
            if (cache.TryGet(index, out var value))
            {
                baseStorageTree.HintGet(index, value);
                _cacheHitHit.Inc();
                return value;
            }

            _cacheHitMiss.Inc();
            byte[] actualValue = baseStorageTree.Get(in index);

            cache.Set(index, actualValue);
            return actualValue;
        }

        public void HintGet(in UInt256 index, byte[]? value)
        {
            cache.Set(index, value);
            baseStorageTree.HintGet(in index, value);
        }

        public byte[] Get(in ValueHash256 hash)
        {
            return baseStorageTree.Get(in hash);
        }
    }

    private sealed class WriteBatchWrapper : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private Dictionary<Address, Account> _changedValues = new();
        private Dictionary<Address, bool> _changedStorage = new();
        private readonly IWorldStateScopeProvider.IWorldStateWriteBatch _baseWriteBatch;
        private readonly SnapshotBundle _snapshotBundle;

        public WriteBatchWrapper(IWorldStateScopeProvider.IWorldStateWriteBatch baseWriteBatch, SnapshotBundle snapshotBundle)
        {
            _baseWriteBatch = baseWriteBatch;
            _snapshotBundle = snapshotBundle;

            _baseWriteBatch.OnAccountChanged += BaseWriteBatchOnOnAccountChanged;
        }

        private void BaseWriteBatchOnOnAccountChanged(object? sender, IWorldStateScopeProvider.AccountChangeEvent e)
        {
            _changedValues[e.Address] = e.Account;
        }

        public void Set(Address key, Account account)
        {
            _baseWriteBatch.Set(key, account);
            _changedValues[key] = account;
        }

        public void Dispose()
        {
            _baseWriteBatch.Dispose();
            _snapshotBundle.ApplyStateChanges(_changedValues);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
        {
            _changedStorage[key] = true;
            return new StorageWriteBatchWrapper(_baseWriteBatch.CreateStorageWriteBatch(key, estimatedEntries), _snapshotBundle.GatherStorageCache(key));
        }

        public event EventHandler<IWorldStateScopeProvider.AccountChangeEvent>? OnAccountChanged
        {
            add => _baseWriteBatch.OnAccountChanged += value;
            remove => _baseWriteBatch.OnAccountChanged -= value;
        }
    }

    private sealed class StorageWriteBatchWrapper(IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch, StorageSnapshotBundle storageSnapshots) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private Dictionary<UInt256, byte[]> _changedValues = new();
        private bool _hasSelfDestruct = false;

        public void Set(in UInt256 index, byte[] value)
        {
            storageWriteBatch.Set(in index, value);
            _changedValues[index] = value;
        }

        public void Clear()
        {
            storageWriteBatch.Clear();
            _hasSelfDestruct = true;
        }

        public void Dispose()
        {
            storageWriteBatch.Dispose();
            storageSnapshots.ApplyStateChanges(_changedValues, _hasSelfDestruct);
        }
    }

    public PreBlockCaches Caches => ((IPreBlockCaches)_baseScopeProvider).Caches;
    public bool IsWarmWorldState => ((IPreBlockCaches)_baseScopeProvider).IsWarmWorldState;
}


public readonly record struct LazySerializeSnapshot(
    StateId From,
    StateId To,
    Dictionary<Address, byte[]> Accounts,
    Dictionary<Address, StorageWrites> Storages)
{
    public Snapshot GetSnapshot()
    {
        Dictionary<Address, Account> accounts = new();
        foreach (var keyValuePair in Accounts)
        {
            try
            {
                if (keyValuePair.Value == null || keyValuePair.Value.Length == 0)
                {
                    accounts[keyValuePair.Key] = null;
                }
                else
                {
                    accounts[keyValuePair.Key] = AccountDecoder.Instance.Decode(keyValuePair.Value);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error decoding {keyValuePair.Value?.ToHexString()}, {ex}");
                throw;
            }
        }


        return new Snapshot(
            From, To,
            accounts,
            Storages
        );
    }
}


public readonly record struct Snapshot(
    StateId From,
    StateId To,
    Dictionary<Address, Account> Accounts,
    Dictionary<Address, StorageWrites> Storages)
{
    public LazySerializeSnapshot ToSerializeSnapshot() => new LazySerializeSnapshot(From, To, Accounts.ToDictionary(
        (kv) => kv.Key,
        (kv) =>
        {
            if (kv.Value is null) return null;
            return AccountDecoder.Instance.Encode(kv.Value)?.Bytes;
        }), Storages);
}

public record struct StorageWrites(Dictionary<UInt256, byte[]> Slots, bool HasSelfDestruct);
