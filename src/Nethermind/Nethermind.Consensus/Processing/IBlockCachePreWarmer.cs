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
    /// Starts a speculative warming session against <paramref name="head"/>'s post-state, warming batches from
    /// <paramref name="nextDelta"/> until <paramref name="cancellationToken"/> fires or the next block enters processing.
    /// If that block builds on <paramref name="head"/> under the same fork, <see cref="PreWarmCaches"/> reuses the warmed
    /// caches. <paramref name="generation"/> is a monotonic head counter; a session is dropped if a newer one has already
    /// started. Returns the session task.
    /// </summary>
    Task StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, long generation, Func<CancellationToken, Block?> nextDelta, int idlePassDelayMs, CancellationToken cancellationToken);
}
