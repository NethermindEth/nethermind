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

    /// <summary>
    /// Speculatively executes a future block's transactions against <paramref name="baseBlock"/>'s state, purely
    /// for the read side effects: the touched state is pulled into the database caches ahead of the executor.
    /// Runs in the background on a separate environment pool that never feeds the per-block caches, so it cannot
    /// affect processing correctness. Fire-and-forget; cancelled via <paramref name="cancellationToken"/>.
    /// </summary>
    void PreWarmLookahead(Block futureBlock, BlockHeader baseBlock, IReleaseSpec spec, CancellationToken cancellationToken)
    {
    }
}
