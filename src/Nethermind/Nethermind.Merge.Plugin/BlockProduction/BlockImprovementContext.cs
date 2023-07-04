// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class BlockImprovementContext : IBlockImprovementContext
{
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly FeesTracer _feesTracer = new();

    public BlockImprovementContext(Block currentBestBlock,
        IManualBlockProductionTrigger blockProductionTrigger,
        TimeSpan timeout,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime)
    {
        _cancellationTokenSource = new CancellationTokenSource(timeout);
        CurrentBestBlock = currentBestBlock;
        StartDateTime = startDateTime;
        ImprovementTask = blockProductionTrigger
            .BuildBlock(parentHeader, _cancellationTokenSource.Token, _feesTracer, payloadAttributes)
            .ContinueWith(SetCurrentBestBlock, _cancellationTokenSource.Token);
    }

    public Task<Block?> ImprovementTask { get; }

    public Block? CurrentBestBlock { get; private set; }
    public UInt256 BlockFees { get; private set; }

    private Block? SetCurrentBestBlock(Task<Block?> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            if (task.Result is not null)
            {
                CurrentBestBlock = task.Result;
                BlockFees = _feesTracer.Fees;
            }
        }

        return task.Result;
    }

    public bool Disposed { get; private set; }
    public DateTimeOffset StartDateTime { get; }

    public void Dispose()
    {
        Disposed = true;
        CancellationTokenExtensions.CancelDisposeAndClear(ref _cancellationTokenSource);
    }
}
