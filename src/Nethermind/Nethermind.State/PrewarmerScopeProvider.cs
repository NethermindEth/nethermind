// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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
    bool populatePreBlockCache = true
) : IWorldStateScopeProvider, IPreBlockCaches
{
    internal static Histogram _timer = DevMetric.Factory.CreateHistogram("prewarmer_dirty_get", "timer",
        new HistogramConfiguration()
        {
            LabelNames = ["part", "is_prewarmer"],
            // Buckets = Histogram.PowersOfTenDividedBuckets(5, 10, 5)
            Buckets = [1]
        });

    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => new ScopeWrapper(baseProvider.BeginScope(baseBlock), preBlockCaches, populatePreBlockCache);

    public PreBlockCaches? Caches => preBlockCaches;
    public bool IsWarmWorldState => !populatePreBlockCache;

    private sealed class ScopeWrapper(
        IWorldStateScopeProvider.IScope baseScope,
        PreBlockCaches preBlockCaches,
        bool populatePreBlockCache)
        : IWorldStateScopeProvider.IScope
    {
        private Histogram.Child _addressGetHit = _timer.WithLabels("address_hit", populatePreBlockCache.ToString());
        private Histogram.Child _addressGetMiss = _timer.WithLabels("address_miss", populatePreBlockCache.ToString());
        private Histogram.Child _addressGetHint = _timer.WithLabels("address_get_hint", populatePreBlockCache.ToString());

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
            long sw = Stopwatch.GetTimestamp();
            if (populatePreBlockCache)
            {
                long priorReads = Metrics.ThreadLocalStateTreeReads;
                Account? account = preBlockCache.GetOrAdd(address, GetFromBaseTree);

                if (Metrics.ThreadLocalStateTreeReads == priorReads)
                {
                    _addressGetHit.Observe(Stopwatch.GetTimestamp() - sw);
                    Metrics.IncrementStateTreeCacheHits();
                }
                else
                {
                    _addressGetMiss.Observe(Stopwatch.GetTimestamp() - sw);
                }
                return account;
            }
            else
            {
                if (preBlockCache?.TryGetValue(addressAsKey, out Account? account) ?? false)
                {
                    _addressGetHit.Observe(Stopwatch.GetTimestamp() - sw);
                    sw = Stopwatch.GetTimestamp();
                    baseScope.HintGet(address, account);
                    _addressGetHint.Observe(Stopwatch.GetTimestamp() - sw);
                    Metrics.IncrementStateTreeCacheHits();
                }
                else
                {
                    account = GetFromBaseTree(addressAsKey);
                    _addressGetMiss.Observe(Stopwatch.GetTimestamp() - sw);
                }
                return account;
            }
        }

        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);
        public void HintSet(Address address) => baseScope.HintSet(address);

        private Account? GetFromBaseTree(AddressAsKey address)
        {
            return baseScope.Get(address);
        }
    }

    private sealed class StorageTreeWrapper(
        IWorldStateScopeProvider.IStorageTree baseStorageTree,
        ConcurrentDictionary<StorageCell, byte[]> preBlockCache,
        Address address,
        bool populatePreBlockCache
    ) : IWorldStateScopeProvider.IStorageTree
    {
        private Histogram.Child _slotGetHit = _timer.WithLabels("slot_get_hit", populatePreBlockCache.ToString());
        private Histogram.Child _slotGetHint = _timer.WithLabels("slot_get_hint", populatePreBlockCache.ToString());
        private Histogram.Child _slotGetMiss = _timer.WithLabels("slot_get_miss", populatePreBlockCache.ToString());
        public Hash256 RootHash => baseStorageTree.RootHash;

        public byte[] Get(in UInt256 index)
        {
            StorageCell storageCell = new StorageCell(address, in index); // TODO: Make the dictionary use UInt256 directly
            long sw = Stopwatch.GetTimestamp();
            if (populatePreBlockCache)
            {
                long priorReads = Db.Metrics.ThreadLocalStorageTreeReads;

                byte[] value = preBlockCache.GetOrAdd(storageCell, LoadFromTreeStorage);

                if (Db.Metrics.ThreadLocalStorageTreeReads == priorReads)
                {
                    _slotGetHit.Observe(Stopwatch.GetTimestamp() - sw);
                    // Read from Concurrent Cache
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else
                {
                    _slotGetMiss.Observe(Stopwatch.GetTimestamp() - sw);
                }
                return value;
            }
            else
            {
                if (preBlockCache?.TryGetValue(storageCell, out byte[] value) ?? false)
                {
                    long sw2 = Stopwatch.GetTimestamp();
                    _slotGetHit.Observe(sw2 - sw);
                    baseStorageTree.HintGet(index, value);
                    _slotGetHint.Observe(Stopwatch.GetTimestamp() - sw2);
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else
                {
                    value = LoadFromTreeStorage(storageCell);
                    _slotGetMiss.Observe(Stopwatch.GetTimestamp() - sw);
                }
                return value;
            }
        }

        public void HintGet(in UInt256 index, byte[]? value) => baseStorageTree.HintGet(in index, value);

        private byte[] LoadFromTreeStorage(StorageCell storageCell)
        {
            Db.Metrics.IncrementStorageTreeReads();

            return !storageCell.IsHash
                ? baseStorageTree.Get(storageCell.Index)
                : baseStorageTree.Get(storageCell.Hash);
        }

        public byte[] Get(in ValueHash256 hash) =>
            // Not a critical path. so we just forward for simplicity
            baseStorageTree.Get(in hash);
    }
}
