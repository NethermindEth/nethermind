// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public bool HasRoot(BlockHeader? baseBlock)
    {
        return baseProvider.HasRoot(baseBlock);
    }

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        return new ScopeWrapper(baseProvider.BeginScope(baseBlock), preBlockCaches, populatePreBlockCache);
    }

    public PreBlockCaches? Caches => preBlockCaches;
    public bool IsWarmWorldState => !populatePreBlockCache;

    private class ScopeWrapper(
        IWorldStateScopeProvider.IScope baseScope,
        PreBlockCaches preBlockCaches,
        bool populatePreBlockCache)
        : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IStateTree _wrappedStateTree = new StateTreeWrapper(baseScope.StateTree, preBlockCaches.StateCache, populatePreBlockCache);

        public void Dispose()
        {
            baseScope.Dispose();
        }

        public IWorldStateScopeProvider.IStateTree StateTree => _wrappedStateTree;

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            return new StorageTreeWrapper(
                baseScope.CreateStorageTree(address),
                preBlockCaches.StorageCache,
                address,
                populatePreBlockCache);
        }

        public void Commit(long blockNumber)
        {
            baseScope.Commit(blockNumber);
        }
    }

    private class StateTreeWrapper(
        IWorldStateScopeProvider.IStateTree baseStateTree,
        ConcurrentDictionary<AddressAsKey, Account> preBlockCache,
        bool populatePreBlockCache
    ) : IWorldStateScopeProvider.IStateTree
    {
        public Hash256 RootHash => baseStateTree.RootHash;

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
            return baseStateTree.Get(address);
        }

        public IWorldStateScopeProvider.IStateSetter BeginSet(int estimatedEntries)
        {
            return baseStateTree.BeginSet(estimatedEntries);
        }

        public void UpdateRootHash()
        {
            baseStateTree.UpdateRootHash();
        }
    }

    private class StorageTreeWrapper(
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

        public byte[] Get(in ValueHash256 hash)
        {
            // Not a critical path. so we just forward for simplicity
            return baseStorageTree.Get(in hash);
        }

        public void Clear()
        {
            baseStorageTree.Clear();
        }

        public IWorldStateScopeProvider.IStorageSetter BeginSet(int estimatedEntries)
        {
            return baseStorageTree.BeginSet(estimatedEntries);
        }

        public void UpdateRootHash(bool canBeParallel = true)
        {
            baseStorageTree.UpdateRootHash(canBeParallel);
        }
    }
}
