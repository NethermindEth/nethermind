// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class BlockImprovementContext : IBlockImprovementContext
{
    private CancellationTokenSource? _timedCancellation;
    private readonly FeesTracer _feesTracer = new();

    public BlockImprovementContext(Block currentBestBlock,
        IBlockProducer blockProducer,
        TimeSpan timeout,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        UInt256 currentBlockFees,
        CancellationToken token)
    {
        _timedCancellation = new CancellationTokenSource(timeout);
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, _timedCancellation.Token);

        CurrentBestBlock = currentBestBlock;
        BlockFees = currentBlockFees;
        StartDateTime = startDateTime;
        ImprovementTask = Task.Run(() => blockProducer
            .BuildBlock(parentHeader, _feesTracer, payloadAttributes, cancellationTokenSource.Token)
            .ContinueWith(SetCurrentBestBlock, cancellationTokenSource.Token), token);
    }

    public Task<Block?> ImprovementTask { get; }

    public Block? CurrentBestBlock { get; private set; }
    public UInt256 BlockFees { get; private set; }

    private Block? SetCurrentBestBlock(Task<Block?> task)
    {
        Block? block = task.Result;
        if (task.IsCompletedSuccessfully)
        {
            if (block is not null)
            {
                UInt256 fees = _feesTracer.Fees;
                if (CurrentBestBlock is null ||
                    fees > BlockFees ||
                    (fees == BlockFees && block.GasUsed > CurrentBestBlock.GasUsed))
                {
                    // Only update block if block has actually improved.
                    CurrentBestBlock = block;
                    BlockFees = fees;
                }
            }
        }

        return CurrentBestBlock;
    }

    public bool Disposed { get; private set; }
    public DateTimeOffset StartDateTime { get; }

    public void Dispose()
    {
        Disposed = true;
        CancellationTokenExtensions.CancelDisposeAndClear(ref _timedCancellation);
    }
}
