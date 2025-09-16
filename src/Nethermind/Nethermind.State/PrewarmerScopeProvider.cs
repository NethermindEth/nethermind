// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum, Action<Address, Account> onAccountUpdated)
        {
            return baseScope.StartWriteBatch(estimatedAccountNum, onAccountUpdated);
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
                    Metrics.IncrementStateTreeCacheHits();
                }
                else
                {
                    account = GetFromBaseTree(addressAsKey);
                }
                return account;
            }
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
        public bool WasEmptyTree => baseStorageTree.WasEmptyTree;

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
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else
                {
                    value = LoadFromTreeStorage(storageCell);
                }
                return value;
            }
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
