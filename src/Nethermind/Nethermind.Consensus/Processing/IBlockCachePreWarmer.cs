// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

public interface IBlockCachePreWarmer : IDisposable
{
    Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists);
    CacheType ClearCaches();
    bool IsBalReadWarmingEnabled(IReleaseSpec spec);

    /// <summary>Sender-free warm (tx.To + EIP-2930 lists) kicked off before sender recovery; overlaps block-prep work. No-op unless Blocks.PreWarmEarlyKickoff.</summary>
    void PreWarmCachesEarly(Block suggestedBlock, BlockHeader parent) { }

    /// <summary>Blocks until the previous block's queued cache clear has completed.</summary>
    void WaitForCacheClear() { }

    /// <summary>Queues the post-block cache clear to run after <paramref name="preWarmTask"/> completes (or clears synchronously when null).</summary>
    void QueueClearCaches(Task? preWarmTask) => ClearCaches();
}
