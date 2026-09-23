// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class BlockImprovementContext : IBlockImprovementContext
{
    private readonly SharedCancellationTokenSource _improvementCancellation;
    private CancellationTokenSource? _timeOutCancellation;
    private CancellationTokenSource? _linkedCancellation;
    private readonly FeesTracer _feesTracer = new();
    private volatile BlockProductionSnapshot _best;

    public BlockImprovementContext(Block currentBestBlock,
        IBlockProducer blockProducer,
        TimeSpan timeout,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        UInt256 currentBlockFees,
        SharedCancellationTokenSource cts)
    {
        _improvementCancellation = cts;
        _timeOutCancellation = new CancellationTokenSource(timeout);
        _best = new(currentBestBlock, currentBlockFees);
        StartDateTime = startDateTime;

        _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _timeOutCancellation.Token);
        CancellationToken ct = _linkedCancellation.Token;
        // Task.Run so doesn't block FCU response while first block is being produced
        ImprovementTask = Task.Run(() => blockProducer
            .BuildBlock(parentHeader, _feesTracer, payloadAttributes, IBlockProducer.Flags.None, ct)
            .ContinueWith(SetCurrentBestBlock));
    }

    public Task<Block?> ImprovementTask { get; }

    public BlockProductionSnapshot Best => _best;

    private Block? SetCurrentBestBlock(Task<Block?> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            Block? block = task.Result;
            if (block is not null)
            {
                UInt256 fees = _feesTracer.Fees;
                BlockProductionSnapshot best = _best;
                if (best.CurrentBestBlock is null ||
                    fees > best.BlockFees ||
                    (fees == best.BlockFees && block.GasUsed > best.CurrentBestBlock.GasUsed))
                {
                    // Only update block if block has actually improved.
                    _best = new(block, fees);
                }
            }
        }

        return _best.CurrentBestBlock;
    }

    public bool Disposed { get; private set; }
    public DateTimeOffset StartDateTime { get; }

    public void CancelOngoingImprovements() => _improvementCancellation.CancelAndDispose();

    public void Dispose()
    {
        Disposed = true;
        CancellationTokenExtensions.CancelDisposeAndClear(ref _linkedCancellation);
        CancellationTokenExtensions.CancelDisposeAndClear(ref _timeOutCancellation);
    }
}
