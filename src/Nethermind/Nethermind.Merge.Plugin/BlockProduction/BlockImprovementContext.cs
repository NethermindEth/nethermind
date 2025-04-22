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
    private readonly CancellationTokenSource _improvementCancellation;
    private CancellationTokenSource? _timeOutCancellation;
    private CancellationTokenSource? _linkedCancellation;
    private readonly FeesTracer _feesTracer = new();

    public BlockImprovementContext(Block currentBestBlock,
        IBlockProducer blockProducer,
        TimeSpan timeout,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        UInt256 currentBlockFees,
        CancellationTokenSource cts)
    {
        _improvementCancellation = cts;
        _timeOutCancellation = new CancellationTokenSource(timeout);
        CurrentBestBlock = currentBestBlock;
        BlockFees = currentBlockFees;
        StartDateTime = startDateTime;

        _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _timeOutCancellation.Token);
        CancellationToken ct = _linkedCancellation.Token;
        // Task.Run so doesn't block FCU response while first block is being produced
        ImprovementTask = Task.Run(() => blockProducer
            .BuildBlock(parentHeader, _feesTracer, payloadAttributes, ct)
            .ContinueWith(SetCurrentBestBlock));
    }

    public Task<Block?> ImprovementTask { get; }

    public Block? CurrentBestBlock { get; private set; }
    public UInt256 BlockFees { get; private set; }

    private Block? SetCurrentBestBlock(Task<Block?> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            Block? block = task.Result;
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

    public void CancelOngoingImprovements() => _improvementCancellation.Cancel();

    public void Dispose()
    {
        Disposed = true;
        CancellationTokenExtensions.CancelDisposeAndClear(ref _linkedCancellation);
        CancellationTokenExtensions.CancelDisposeAndClear(ref _timeOutCancellation);
    }
}
