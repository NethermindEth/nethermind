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

    /// <summary>Clears the block-processing caches.</summary>
    /// <param name="retainForNextBlock">
    /// Experiment (NM_XP_XBLOCK): the just-processed block committed successfully, so its post-state is a valid
    /// parent state for the next block. Publishes a handoff marker instead of clearing, so a next block whose
    /// parent matches reuses the entries. Ignored unless the cross-block experiment is enabled.
    /// </param>
    /// <returns>
    /// The built-in implementation only reports <see cref="CacheType.Rlp"/>, which means that RLP node-storage caching
    /// was enabled, not necessarily that it contained entries. The storage, state, and precompile caches do not report
    /// whether they contained entries.
    /// </returns>
    CacheType ClearCaches(bool retainForNextBlock = false);

    bool IsBalReadWarmingEnabled(IReleaseSpec spec);

    /// <summary>
    /// Speculatively warms against <paramref name="head"/> from <paramref name="nextDelta"/> until cancelled or the next
    /// block enters processing; <paramref name="generation"/> drops the session if a newer head has already started one.
    /// </summary>
    Task StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, long generation, Func<CancellationToken, Block?> nextDelta, int idlePassDelayMs, CancellationToken cancellationToken);
}
