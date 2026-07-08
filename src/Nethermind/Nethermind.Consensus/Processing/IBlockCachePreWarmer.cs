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
    /// Starts a speculative warming session against <paramref name="head"/>'s post-state. Runs repeated delta passes —
    /// pulling the next batch of not-yet-warmed transactions from <paramref name="nextDelta"/> — accumulating committed
    /// base-state reads into the caches until cancelled (e.g. when the next block enters processing). If the next block
    /// builds on <paramref name="head"/> under the same fork, <see cref="PreWarmCaches"/> reuses the warmed entries
    /// instead of clearing them. Cancels and replaces any previously started session.
    /// </summary>
    /// <param name="nextDelta">Returns the next block of transactions to warm (deduped by the caller), or null when
    /// there is nothing new to warm right now; invoked repeatedly on a background thread.</param>
    /// <param name="idlePassDelayMs">How long to wait before re-sampling after a pass returns nothing new.</param>
    void StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, Func<CancellationToken, Block?> nextDelta, int idlePassDelayMs);

    /// <summary>
    /// Requests cancellation of an in-flight speculative pass without waiting for it to finish. Lock-free and safe to
    /// call when no pass is running.
    /// </summary>
    void CancelSpeculativePreWarm();
}
