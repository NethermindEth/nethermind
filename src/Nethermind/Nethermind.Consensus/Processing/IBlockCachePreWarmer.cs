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
    /// Notify prewarmer that the main processor has finished executing transaction at <paramref name="txIndex"/>.
    /// Prewarmer threads will skip this and earlier transactions to avoid redundant work.
    /// </summary>
    void NotifyTxExecuted(int txIndex) { }
}
