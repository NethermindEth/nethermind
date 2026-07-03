// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.StateDiffArchive.Storage;

namespace Nethermind.StateDiffArchive.Replay;

/// <summary>
/// Decorates the block cache prewarmer during replay: a block that will be replayed from the archive runs no
/// EVM, so there is nothing to prewarm — skip it. Blocks past the archive (which fall through to real
/// execution) are prewarmed normally by delegating to the inner prewarmer.
/// </summary>
public sealed class ReplayPrewarmGate(IBlockCachePreWarmer inner, StateDiffStore store) : IBlockCachePreWarmer
{
    public Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
        => store.HasRecord(suggestedBlock.Number)
            ? Task.CompletedTask
            : inner.PreWarmCaches(suggestedBlock, parent, spec, cancellationToken, systemAccessLists);

    public CacheType ClearCaches() => inner.ClearCaches();

    public bool IsBalReadWarmingEnabled(IReleaseSpec spec) => inner.IsBalReadWarmingEnabled(spec);

    public void Dispose() => inner.Dispose();
}
