// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Consensus.Processing;

public class NoBlockCachePreWarmer(NodeStorageCache nodeStorageCache, PreBlockCaches preBlockCaches, ILogManager logManager) : IBlockCachePreWarmer
{
    protected readonly ILogger _logger = logManager.GetClassLogger();

    public virtual Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec,
        CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
    {
        CacheType result = ClearCaches(true);
        if (result != default)
        {
            if (_logger.IsWarn) _logger.Warn($"Caches {result} are not empty. Clearing them.");
        }

        return Task.CompletedTask;
    }

    public CacheType ClearCaches() => ClearCaches(false);

    private CacheType ClearCaches(bool enabled)
    {
        if (_logger.IsDebug) _logger.Debug("Clearing caches");
        CacheType cachesCleared = preBlockCaches.ClearCaches();
        cachesCleared |= nodeStorageCache.ClearCaches(enabled) ? CacheType.Rlp : CacheType.None;
        if (_logger.IsDebug) _logger.Debug($"Cleared caches: {cachesCleared}");
        return cachesCleared;
    }
}
