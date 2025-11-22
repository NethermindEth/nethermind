// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Prometheus;
using Metrics = Nethermind.Db.Metrics;

namespace Nethermind.State;

public class PrewarmerScopeProvider(
    IWorldStateScopeProvider baseProvider,
    PreBlockCaches preBlockCaches,
    bool populatePreBlockCache = true)
    : IWorldStateScopeProvider, IPreBlockCaches
{
    static Counter _prewarmerColdRead = Prometheus.Metrics.CreateCounter("prewarmer_cold_read", "", "type", "is_null");
    internal static Counter _prewarmerHitMissCount = Prometheus.Metrics.CreateCounter("prewarmer_hit_count", "", "type", "hit");
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => new ScopeWrapper(
        baseProvider.BeginScope(baseBlock),
        preBlockCaches,
        populatePreBlockCache);

    public PreBlockCaches? Caches => preBlockCaches;
    public bool IsWarmWorldState => !populatePreBlockCache;

    private sealed class ScopeWrapper(
        IWorldStateScopeProvider.IScope baseScope,
        PreBlockCaches preBlockCaches,
        bool populatePreBlockCache)
        : IWorldStateScopeProvider.IScope
    {
        ConcurrentDictionary<AddressAsKey, Account> preBlockCache = preBlockCaches.StateCache;

        public void Dispose() => baseScope.Dispose();

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            return new StorageTreeWrapper(
                baseScope.CreateStorageTree(address),
                preBlockCaches.StorageCache,
                address,
                populatePreBlockCache);
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
            return baseScope.StartWriteBatch(estimatedAccountNum);
        }

        public void Commit(long blockNumber) => baseScope.Commit(blockNumber);

        public Hash256 RootHash => baseScope.RootHash;

        public void UpdateRootHash()
        {
            baseScope.UpdateRootHash();
        }

        public Account? Get(Address address)
        {
            AddressAsKey addressAsKey = address;
            if (populatePreBlockCache)
            {
                long priorReads = Metrics.ThreadLocalStateTreeReads;
                Account? account = preBlockCache.GetOrAdd(address, GetFromBaseTree);

                if (Metrics.ThreadLocalStateTreeReads == priorReads)
                {
                    Metrics.IncrementStateTreeCacheHits();
                }
                return account;
            }
            else
            {
                if (preBlockCache?.TryGetValue(addressAsKey, out Account? account) ?? false)
                {
                    HintGet(address, account);
                    _stateReadHit.Inc();
                    Metrics.IncrementStateTreeCacheHits();
                }
                else
                {
                    _stateReadMiss.Inc();
                    account = GetFromBaseTree(addressAsKey);
                }
                return account;
            }
        }

        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);

        private static Counter.Child _stateReadHit = _prewarmerHitMissCount.WithLabels("state", "hit");
        private static Counter.Child _stateReadMiss = _prewarmerHitMissCount.WithLabels("state", "miss");
        private static Counter.Child _stateReadTimeNull = _prewarmerColdRead.WithLabels("state", "true");
        private static Counter.Child _stateReadTimeNotNull = _prewarmerColdRead.WithLabels("state", "false");

        private Account? GetFromBaseTree(AddressAsKey address)
        {
            long sw = Stopwatch.GetTimestamp();
            var acc = baseScope.Get(address);
            if (!populatePreBlockCache)
            {
                if (acc is null)
                {
                    _stateReadTimeNull.Inc(Stopwatch.GetTimestamp() - sw);
                }
                else
                {
                    _stateReadTimeNotNull.Inc(Stopwatch.GetTimestamp() - sw);
                }
            }
            return acc;
        }
    }

    private sealed class StorageTreeWrapper(
        IWorldStateScopeProvider.IStorageTree baseStorageTree,
        ConcurrentDictionary<StorageCell, byte[]> preBlockCache,
        Address address,
        bool populatePreBlockCache) : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash => baseStorageTree.RootHash;
        private static Counter.Child _storageReadHit = _prewarmerHitMissCount.WithLabels("storage", "hit");
        private static Counter.Child _storageReadMiss = _prewarmerHitMissCount.WithLabels("storage", "miss");

        public byte[] Get(in UInt256 index)
        {
            StorageCell storageCell = new StorageCell(address, in index); // TODO: Make the dictionary use UInt256 directly
            if (populatePreBlockCache)
            {
                long priorReads = Db.Metrics.ThreadLocalStorageTreeReads;

                byte[] value = preBlockCache.GetOrAdd(storageCell, LoadFromTreeStorage);

                if (Db.Metrics.ThreadLocalStorageTreeReads == priorReads)
                {
                    // Read from Concurrent Cache
                    Db.Metrics.IncrementStorageTreeCache();
                }
                return value;
            }
            else
            {
                if (preBlockCache?.TryGetValue(storageCell, out byte[] value) ?? false)
                {
                    baseStorageTree.HintGet(index, value);

                    HintGet(index, value);
                    _storageReadHit.Inc();
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else
                {
                    _storageReadMiss.Inc();
                    value = LoadFromTreeStorage(storageCell);
                }
                return value;
            }
        }

        public void HintGet(in UInt256 index, byte[]? value) => baseStorageTree.HintGet(in index, value);

        private static Counter.Child _storageReadTimeNull = _prewarmerColdRead.WithLabels("storage", "true");
        private static Counter.Child _storageReadTimeNotNull = _prewarmerColdRead.WithLabels("storage", "false");
        private byte[] LoadFromTreeStorage(StorageCell storageCell)
        {
            Db.Metrics.IncrementStorageTreeReads();

            long sw = Stopwatch.GetTimestamp();
            byte[]? value = !storageCell.IsHash
                ? baseStorageTree.Get(storageCell.Index)
                : baseStorageTree.Get(storageCell.Hash);
            if (!populatePreBlockCache)
            {
                if (value is null) _storageReadTimeNull.Inc(Stopwatch.GetTimestamp() - sw);

                else _storageReadTimeNotNull.Inc(Stopwatch.GetTimestamp() - sw);
            }

            return value;
        }

        public byte[] Get(in ValueHash256 hash) =>
            // Not a critical path. so we just forward for simplicity
            baseStorageTree.Get(in hash);
    }
}
