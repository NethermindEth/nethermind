// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class NoBlockImprovementContext : NoBlockProductionContext, IBlockImprovementContext
{
    public NoBlockImprovementContext(Block? currentBestBlock, UInt256 blockFees, DateTimeOffset startDateTime, CancellationTokenSource cts)
        : base(currentBestBlock, blockFees)
    {
        StartDateTime = startDateTime;

        Disposed = true;
        ImprovementTask = Task.FromResult(currentBestBlock);
        CancellationTokenSource = cts;
    }

    void IDisposable.Dispose() { }

    public void CancelOngoingImprovements() => CancellationTokenSource.Cancel();

    public bool Disposed { get; }

    public Task<Block?> ImprovementTask { get; }
    public DateTimeOffset StartDateTime { get; }
    public CancellationTokenSource CancellationTokenSource { get; }
}
