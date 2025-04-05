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
using Nethermind.Merge.Plugin.Data;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostBlockImprovementContext : IBlockImprovementContext
{
    private readonly IBoostRelay _boostRelay;
    private readonly IStateReader _stateReader;
    private readonly FeesTracer _feesTracer = new();
    private readonly CancellationTokenSource _improvementCancellation;
    private CancellationTokenSource? _timeOutCancellation;
    private CancellationTokenSource? _linkedCancellation;

    public BoostBlockImprovementContext(Block currentBestBlock,
        IBlockProducer blockProducer,
        TimeSpan timeout,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        IBoostRelay boostRelay,
        IStateReader stateReader,
        DateTimeOffset startDateTime,
        CancellationTokenSource cts)
    {
        _boostRelay = boostRelay;
        _stateReader = stateReader;
        _improvementCancellation = cts;
        _timeOutCancellation = new CancellationTokenSource(timeout);
        _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _timeOutCancellation.Token);
        CurrentBestBlock = currentBestBlock;
        StartDateTime = startDateTime;
        ImprovementTask = StartImprovingBlock(blockProducer, parentHeader, payloadAttributes, _linkedCancellation.Token);
    }

    private async Task<Block?> StartImprovingBlock(
        IBlockProducer blockProducer,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        CancellationToken cancellationToken)
    {

        payloadAttributes = await _boostRelay.GetPayloadAttributes(payloadAttributes, cancellationToken);
        _stateReader.TryGetAccount(parentHeader.StateRoot!, payloadAttributes.SuggestedFeeRecipient, out AccountStruct account);
        UInt256 balanceBefore = account.Balance;
        Block? block = await blockProducer.BuildBlock(parentHeader, _feesTracer, payloadAttributes, cancellationToken);
        if (block is not null)
        {
            CurrentBestBlock = block;
            BlockFees = _feesTracer.Fees;
            _stateReader.TryGetAccount(parentHeader.StateRoot!, payloadAttributes.SuggestedFeeRecipient, out account);
            await _boostRelay.SendPayload(new BoostExecutionPayloadV1 { Block = ExecutionPayload.Create(block), Profit = account.Balance - balanceBefore }, cancellationToken);
        }

        return CurrentBestBlock;
    }

    public Task<Block?> ImprovementTask { get; }
    public Block? CurrentBestBlock { get; private set; }
    public UInt256 BlockFees { get; private set; }
    public bool Disposed { get; private set; }
    public DateTimeOffset StartDateTime { get; }

    public void Dispose()
    {
        Disposed = true;
        CancellationTokenExtensions.CancelDisposeAndClear(ref _linkedCancellation);
        CancellationTokenExtensions.CancelDisposeAndClear(ref _timeOutCancellation);
    }

    public void CancelOngoingImprovements() => _improvementCancellation.Cancel();
}
