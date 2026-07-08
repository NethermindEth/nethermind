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
    /// Speculatively warms the caches against <paramref name="head"/>'s post-state using the transactions of
    /// <paramref name="speculativeBlock"/> (built from the mempool). If the next block to be processed builds on
    /// <paramref name="head"/> under the same fork, <see cref="PreWarmCaches"/> reuses the warmed entries instead of
    /// clearing them. Cancels and replaces any previously started speculative pass.
    /// </summary>
    void StartSpeculativePreWarm(Block speculativeBlock, BlockHeader head, IReleaseSpec spec);

    /// <summary>
    /// Requests cancellation of an in-flight speculative pass without waiting for it to finish. Lock-free and safe to
    /// call when no pass is running.
    /// </summary>
    void CancelSpeculativePreWarm();
}
