// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State;

public class ResourcePool
{

    private Dictionary<IFlatDiffRepository.SnapshotBundleUsage, ObjectPool<SnapshotContent>> _snapshotPools = new()
    {
        { IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing , new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing)) },
        { IFlatDiffRepository.SnapshotBundleUsage.ReadOnlyProcessingEnv , new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(IFlatDiffRepository.SnapshotBundleUsage.ReadOnlyProcessingEnv)) },
        { IFlatDiffRepository.SnapshotBundleUsage.StateReader , new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(IFlatDiffRepository.SnapshotBundleUsage.StateReader)) },
        { IFlatDiffRepository.SnapshotBundleUsage.Compactor , new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(IFlatDiffRepository.SnapshotBundleUsage.Compactor)) },
    };

    private Dictionary<IFlatDiffRepository.SnapshotBundleUsage, ObjectPool<CachedResource>> _cachedResourcePools = new()
    {
        { IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing , new DefaultObjectPool<CachedResource>(new CachedResourcePolicy()) },
        { IFlatDiffRepository.SnapshotBundleUsage.ReadOnlyProcessingEnv , new DefaultObjectPool<CachedResource>(new CachedResourcePolicy()) },
        { IFlatDiffRepository.SnapshotBundleUsage.StateReader , new DefaultObjectPool<CachedResource>(new CachedResourcePolicy()) },
        { IFlatDiffRepository.SnapshotBundleUsage.Compactor , new DefaultObjectPool<CachedResource>(new CachedResourcePolicy()) }
    };

    private static Counter _createdSnapshotContent = DevMetric.Factory.CreateCounter("resourcepool_created_snapshot_content", "created snapshot content", "compacted");

    private class SnapshotContentPolicy(IFlatDiffRepository.SnapshotBundleUsage usage) : IPooledObjectPolicy<SnapshotContent>
    {
        public SnapshotContent Create()
        {
            _createdSnapshotContent.WithLabels(usage.ToString()).Inc();
            return new SnapshotContent(
                Accounts: new ConcurrentDictionary<AddressAsKey, Account?>(),
                Storages: new ConcurrentDictionary<(AddressAsKey, UInt256), byte[]?>(),
                SelfDestructedStorageAddresses: new ConcurrentDictionary<AddressAsKey, bool>(),
                StateNodes: new ConcurrentDictionary<TreePath, TrieNode>(),
                StorageNodes: new ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode>()
            );
        }

        public bool Return(SnapshotContent obj)
        {
            obj.Reset();
            return true;
        }
    }

    private class CachedResourcePolicy() : IPooledObjectPolicy<CachedResource>
    {
        public CachedResource Create()
        {
            return new CachedResource(
                new ConcurrentDictionary<TreePath, TrieNode>(),
                new ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode>(),
                new ConcurrentDictionary<(AddressAsKey, UInt256?), bool>()
            );
        }

        public bool Return(CachedResource obj)
        {
            obj.Clear();
            return true;
        }
    }

    public SnapshotContent GetSnapshotContent(IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        return _snapshotPools[usage].Get();
    }

    public void ReturnSnapshotContent(IFlatDiffRepository.SnapshotBundleUsage usage, SnapshotContent snapshotContent)
    {
        _snapshotPools[usage].Return(snapshotContent);
    }

    public ObjectPool<SnapshotContent> GetSnapshotPool(IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        return _snapshotPools[usage];
    }

    public CachedResource GetCachedResource(IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        return _cachedResourcePools[usage].Get();
    }

    public void ReturnCachedResource(IFlatDiffRepository.SnapshotBundleUsage usage, CachedResource cachedResource)
    {
        _cachedResourcePools[usage].Return(cachedResource);
    }
}
