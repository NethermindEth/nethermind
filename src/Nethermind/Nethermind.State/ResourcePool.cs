// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State;

public class ResourcePool
{
    private ObjectPool<SnapshotContent> _snapshotPool = new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(true));
    private ObjectPool<SnapshotContent> _compactedSnapshotPool = new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(true));
    private ObjectPool<HintResource> _hintResourcePool = new DefaultObjectPool<HintResource>(new HintResourcePolicy());

    public ObjectPool<SnapshotContent> SnapshotPool => _snapshotPool;
    public ObjectPool<SnapshotContent> CompactedSnapshotPool => _compactedSnapshotPool;

    private class SnapshotContentPolicy(bool allow) : IPooledObjectPolicy<SnapshotContent>
    {
        public SnapshotContent Create()
        {
            return new SnapshotContent(
                Accounts: new ConcurrentDictionary<AddressAsKey, Account?>(),
                Storages: new ConcurrentDictionary<(AddressAsKey, UInt256), byte[]?>(),
                SelfDestructedStorageAddresses: new ConcurrentDictionary<AddressAsKey, bool>(),
                TrieNodes: new ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode>()
            );
        }

        public bool Return(SnapshotContent obj)
        {
            obj.Reset();
            return allow;
        }
    }

    private class HintResourcePolicy() : IPooledObjectPolicy<HintResource>
    {
        public HintResource Create()
        {
            return new HintResource(
                new ConcurrentDictionary<AddressAsKey, Account>(),
                new ConcurrentDictionary<(AddressAsKey, UInt256), byte[]>()
            );
        }

        public bool Return(HintResource obj)
        {
            obj.Clear();
            return true;
        }
    }

    public SnapshotContent GetSnapshotContent()
    {
        return _snapshotPool.Get();
    }

    public void ReturnSnapshotContent(SnapshotContent snapshotContent)
    {
        _snapshotPool.Return(snapshotContent);
    }

    public SnapshotContent GetCompactedSnapshotPool()
    {
        return _compactedSnapshotPool.Get();
    }

    public HintResource GetHintResource()
    {
        return _hintResourcePool.Get();
    }

    public void ReturnHintResource(HintResource hintResource)
    {
        _hintResourcePool.Return(hintResource);
    }
}
