// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>
/// Mainly to make it easier for test
/// </summary>
public class ReadOnlyWorldStateManager(
    ReadOnlyWorldStateProvider globalWorldStateProvider,
    IDbProvider dbProvider,
    IReadOnlyTrieStore readOnlyTrieStore,
    ILogManager logManager,
    PreBlockCaches? preBlockCaches = null) : IWorldStateManager
{
    public virtual IWorldStateProvider GlobalWorldStateProvider => globalWorldStateProvider;
    public PreBlockCaches? Caches => preBlockCaches;

    public IWorldStateProvider CreateResettableWorldStateProvider()
    {
        ITrieStore preCachedTrieStore = Caches is null
            ? readOnlyTrieStore
            : new PreCachedTrieStore(readOnlyTrieStore, Caches.RlpCache);

        return new WorldStateProvider(preCachedTrieStore, readOnlyTrieStore, dbProvider, logManager, Caches);
    }

    public virtual event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new InvalidOperationException("Unsupported operation");
        remove => throw new InvalidOperationException("Unsupported operation");
    }

    public Task ClearCachesInBackground()
    {
        return Caches?.ClearCachesInBackground() ?? Task.CompletedTask;
    }

    public bool ClearCache()
    {
        return Caches?.ClearImmediate() == true;
    }
}
