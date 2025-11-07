// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

internal sealed class PerContractState
{
    private static readonly Func<StorageCell, PerContractState, byte[]> _loadFromTreeStorageFunc = LoadFromTreeStorage;

    private readonly DefaultableDictionary BlockChange = new();
    private PersistentStorageProvider _provider;
    private Address _address;
    private StorageTree? StorageTree;
    private bool _wasWritten = false;

    private PerContractState(Address address, PersistentStorageProvider provider) => Initialize(address, provider);

    private void Initialize(Address address, PersistentStorageProvider provider)
    {
        _address = address;
        _provider = provider;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureStorageTree()
    {
        if (StorageTree is not null) return;

        CreateStorageTree();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CreateStorageTree()
    {
        // Note: GetStorageRoot is not concurrent safe! And so do this whole method!
        Account? acc = _provider._stateProvider.GetAccount(_address);
        Hash256 storageRoot = acc?.StorageRoot ?? Keccak.EmptyTreeHash;
        bool isEmpty = storageRoot == Keccak.EmptyTreeHash; // We know all lookups will be empty against this tree
        StorageTree = _provider._storageTreeFactory.Create(_address,
            _provider._trieStore.GetTrieStore(_address),
            storageRoot,
            _provider.StateRoot,
            _provider._logManager);

        if (isEmpty && !_wasWritten)
        {
            // Slight optimization that skips the tree
            BlockChange.ClearAndSetMissingAsDefault();
        }
    }

    public Hash256 RootHash
    {
        get
        {
            EnsureStorageTree();
            return StorageTree.RootHash;
        }
    }

    public void Commit()
    {
        EnsureStorageTree();
        StorageTree.Commit();
    }

    public void Clear()
    {
        StorageTree = new StorageTree(_provider._trieStore.GetTrieStore(_address), Keccak.EmptyTreeHash, _provider._logManager);
        BlockChange.ClearAndSetMissingAsDefault();
    }

    public void Reset()
    {
        _address = null;
        _provider = null;
        StorageTree = null;
        Pool.Return(this);
    }

    public void SaveChange(StorageCell storageCell, byte[] value)
    {
        _wasWritten = true;
        ref StorageChangeTrace valueChanges = ref BlockChange.GetValueRefOrAddDefault(storageCell.Index, out bool exists);
        if (!exists)
        {
            valueChanges = new StorageChangeTrace(value);
        }
        else
        {
            valueChanges = new StorageChangeTrace(valueChanges.Before, value);
        }
    }

    public ReadOnlySpan<byte> LoadFromTree(in StorageCell storageCell)
    {
        ref StorageChangeTrace valueChange = ref BlockChange.GetValueRefOrAddDefault(storageCell.Index, out bool exists);
        if (!exists)
        {
            byte[] value = !_provider._populatePreBlockCache ?
                LoadFromTreeReadPreWarmCache(in storageCell) :
                LoadFromTreePopulatePrewarmCache(in storageCell);

            valueChange = new(value, value);
        }
        else
        {
            Db.Metrics.IncrementStorageTreeCache();
        }

        if (!storageCell.IsHash) _provider.PushToRegistryOnly(storageCell, valueChange.After);
        return valueChange.After;
    }

    private byte[] LoadFromTreeReadPreWarmCache(in StorageCell storageCell)
    {
        if (_provider._preBlockCache?.TryGetValue(storageCell, out byte[] value) ?? false)
        {
            Db.Metrics.IncrementStorageTreeCache();
        }
        else
        {
            value = LoadFromTreeStorage(storageCell);
        }
        return value;
    }

    private byte[] LoadFromTreePopulatePrewarmCache(in StorageCell storageCell)
    {
        long priorReads = Db.Metrics.ThreadLocalStorageTreeReads;

        byte[] value = _provider._preBlockCache is not null
            ? _provider._preBlockCache.GetOrAdd(storageCell, _loadFromTreeStorageFunc, this)
            : LoadFromTreeStorage(storageCell);

        if (Db.Metrics.ThreadLocalStorageTreeReads == priorReads)
        {
            // Read from Concurrent Cache
            Db.Metrics.IncrementStorageTreeCache();
        }
        return value;
    }

    private byte[] LoadFromTreeStorage(in StorageCell storageCell)
    {
        Db.Metrics.IncrementStorageTreeReads();

        EnsureStorageTree();
        return !storageCell.IsHash
            ? StorageTree.Get(storageCell.Index)
            : StorageTree.GetArray(storageCell.Hash.Bytes);
    }

    private static byte[] LoadFromTreeStorage(StorageCell storageCell, PerContractState @this)
        => @this.LoadFromTreeStorage(storageCell);

    public (int writes, int skipped) ProcessStorageChanges()
    {
        EnsureStorageTree();

        int writes = 0;
        int skipped = 0;
        if (BlockChange.EstimatedSize < PatriciaTree.MinEntriesToParallelizeThreshold)
        {
            foreach (var kvp in BlockChange)
            {
                byte[] after = kvp.Value.After;
                if (!Bytes.AreEqual(kvp.Value.Before, after) || kvp.Value.IsInitialValue)
                {
                    BlockChange[kvp.Key] = new(after, after);
                    StorageTree.Set(kvp.Key, after);
                    writes++;
                }
                else
                {
                    skipped++;
                }
            }
        }
        else
        {
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkWrite = new(BlockChange.EstimatedSize);

            Span<byte> keyBuf = stackalloc byte[32];
            foreach (KeyValuePair<UInt256, StorageChangeTrace> kvp in BlockChange)
            {
                byte[] after = kvp.Value.After;
                if (!Bytes.AreEqual(kvp.Value.Before, after) || kvp.Value.IsInitialValue)
                {
                    BlockChange[kvp.Key] = new(after, after);

                    StorageTree.ComputeKeyWithLookup(kvp.Key, keyBuf);
                    bulkWrite.Add(StorageTree.CreateBulkSetEntry(new ValueHash256(keyBuf), after));

                    writes++;
                }
                else
                {
                    skipped++;
                }
            }

            StorageTree.BulkSet(bulkWrite);
        }

        if (writes > 0)
        {
            StorageTree.UpdateRootHash(canBeParallel: writes > 64);
        }

        return (writes, skipped);
    }

    public void RemoveStorageTree()
    {
        StorageTree = null;
    }

    internal static PerContractState Rent(Address address, PersistentStorageProvider persistentStorageProvider)
        => Pool.Rent(address, persistentStorageProvider);

    private static class Pool
    {
        [ThreadStatic]
        private static PerContractState? _localFast; // one slot per thread

        private static readonly ConcurrentQueue<PerContractState> _pool = [];
        private static int _poolCount;

        public static PerContractState Rent(Address address, PersistentStorageProvider provider)
        {
            // local ref avoids multiple TLS lookups
            ref PerContractState local = ref _localFast;

            PerContractState item = local;
            if (item is not null)
            {
                local = null;
                item.Initialize(address, provider);
                return item;
            }

            // fallback to global queue
            if (_pool.TryDequeue(out item))
            {
                Interlocked.Decrement(ref _poolCount);
                item.Initialize(address, provider);
                return item;
            }

            return new PerContractState(address, provider);
        }

        public static void Return(PerContractState item)
        {
            const int MaxItemSize = 512;
            const int MaxPooledCount = 2048;

            if (item.BlockChange.Capacity > MaxItemSize)
                return;

            item.BlockChange.Reset();

            // per-thread fast slot - no global accounting
            ref PerContractState local = ref _localFast;
            if (local is null)
            {
                local = item;
                return;
            }

            // shared pool fallback
            if (Interlocked.Increment(ref _poolCount) > MaxPooledCount)
            {
                Interlocked.Decrement(ref _poolCount);
                return;
            }

            _pool.Enqueue(item);
        }
    }
}
