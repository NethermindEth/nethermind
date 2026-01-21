// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

public class PrewarmerScopeProvider(
    IWorldStateScopeProvider baseProvider,
    PreBlockCaches preBlockCaches,
    bool populatePreBlockCache = true
) : IWorldStateScopeProvider, IPreBlockCaches
{
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
        ConcurrentDictionary<AddressAsKey, Account> preBlockCache = preBlockCaches.StateCache;

        public void Dispose()
        {
            long sw = Stopwatch.GetTimestamp();
            baseScope.Dispose();
            Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("dispose", populatePreBlockCache));
        }

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
                    Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("address_hit", populatePreBlockCache));
                    Metrics.IncrementStateTreeCacheHits();
                }
                else
                {
                    Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("address_miss", populatePreBlockCache));
                }
                return account;
            }
            else
            {
                if (preBlockCache?.TryGetValue(addressAsKey, out Account? account) ?? false)
                {
                    Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("address_hit", populatePreBlockCache));
                    sw = Stopwatch.GetTimestamp();
                    baseScope.HintGet(address, account);
                    Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("address_get_hint", populatePreBlockCache));
                    Metrics.IncrementStateTreeCacheHits();
                }
                else
                {
                    account = GetFromBaseTree(addressAsKey);
                    Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("address_miss", populatePreBlockCache));
                }
                return account;
            }
        }

        public void HintGet(Address address, Account? account)
        {
            long sw = Stopwatch.GetTimestamp();
            baseScope.HintGet(address, account);
            Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("address_get_hint", populatePreBlockCache));
        }

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
                    Db.Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("slot_get_hit", populatePreBlockCache));
                    // Read from Concurrent Cache
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else
                {
                    Db.Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("slot_get_miss", populatePreBlockCache));
                }
                return value;
            }
            else
            {
                if (preBlockCache?.TryGetValue(storageCell, out byte[] value) ?? false)
                {
                    Db.Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("slot_get_hit", populatePreBlockCache));
                    sw = Stopwatch.GetTimestamp();
                    baseStorageTree.HintGet(index, value);
                    Db.Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("slot_get_hint", populatePreBlockCache));
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else
                {
                    value = LoadFromTreeStorage(storageCell);
                    Db.Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("slot_get_miss", populatePreBlockCache));
                }
                return value;
            }
        }

        public void HintGet(in UInt256 index, byte[]? value)
        {
            long sw = Stopwatch.GetTimestamp();
            baseStorageTree.HintGet(in index, value);
            Db.Metrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - sw, new PrewarmerGetTimeLabel("slot_get_hint", populatePreBlockCache));
        }

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
