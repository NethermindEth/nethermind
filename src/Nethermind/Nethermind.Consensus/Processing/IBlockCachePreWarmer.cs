// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

public interface IBlockCachePreWarmer : IDisposable
{
    /// <summary>Prepares the block-processing caches for <paramref name="suggestedBlock"/> and, where worthwhile, warms them.</summary>
    /// <remarks>
    /// Called for every block, and owns the pre-block cache lifecycle: before returning, an implementation must either
    /// clear the caches or establish that their contents are valid for <paramref name="suggestedBlock"/>'s parent.
    /// <see cref="ClearCaches"/> is not called before execution, so declining to warm — for a block with too few
    /// transactions to be worth it, say — must not decline that decision.
    /// </remarks>
    /// <returns>A task that completes when warming has finished; the caller awaits it before clearing the caches.</returns>
    Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default);

    /// <summary>Ends a block's use of the block-processing caches once its warming has been joined.</summary>
    /// <remarks>
    /// Drops the per-block precompile results and the RLP node cache. The account and storage caches are kept: the next
    /// <see cref="PreWarmCaches"/> or <see cref="StartSpeculativePreWarm"/> replays the block's committed writes into
    /// them when it builds on that block, and clears them otherwise.
    /// </remarks>
    /// <returns>
    /// The built-in implementation only reports <see cref="CacheType.Rlp"/>, which means that RLP node-storage caching
    /// was enabled, not necessarily that it contained entries. The storage, state, and precompile caches do not report
    /// whether they contained entries.
    /// </returns>
    CacheType ClearCaches();

    bool IsBalReadWarmingEnabled(IReleaseSpec spec);

    /// <summary>
    /// Speculatively warms against <paramref name="head"/> from <paramref name="nextDelta"/> until cancelled or the next
    /// block enters processing; <paramref name="generation"/> drops the session if a newer head has already started one.
    /// </summary>
    Task StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, long generation, Func<CancellationToken, Block?> nextDelta, int idlePassDelayMs, CancellationToken cancellationToken);
}
