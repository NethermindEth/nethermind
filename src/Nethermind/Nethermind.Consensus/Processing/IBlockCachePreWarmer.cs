// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public interface IBlockCachePreWarmer
{
    Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists);
    CacheType ClearCaches();

    /// <summary>
    /// Notifies the prewarmer that the main thread is about to process transaction at <paramref name="txIndex"/>.
    /// Prewarmer threads should skip transactions at or below this index.
    /// </summary>
    void NotifyTransactionProcessing(int txIndex) { }
}
