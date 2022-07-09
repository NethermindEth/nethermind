//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostBlockImprovementContext : IBlockImprovementContext
{
    private readonly IBoostRelay _boostRelay;
    private readonly IStateReader _stateReader;
    private CancellationTokenSource? _cancellationTokenSource;

    public BoostBlockImprovementContext(
        Block currentBestBlock,
        IManualBlockProductionTrigger blockProductionTrigger,
        TimeSpan timeout,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        IBoostRelay boostRelay,
        IStateReader stateReader)
    {
        _boostRelay = boostRelay;
        _stateReader = stateReader;
        _cancellationTokenSource = new CancellationTokenSource(timeout);
        CurrentBestBlock = currentBestBlock;
        ImprovementTask = StartImprovingBlock(blockProductionTrigger, parentHeader, payloadAttributes, _cancellationTokenSource.Token);
    }

    private async Task<Block?> StartImprovingBlock(
        IManualBlockProductionTrigger blockProductionTrigger,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        CancellationToken cancellationToken)
    {

        payloadAttributes = await _boostRelay.GetPayloadAttributes(payloadAttributes, cancellationToken);
        UInt256 balanceBefore = _stateReader.GetAccount(parentHeader.StateRoot!, payloadAttributes.SuggestedFeeRecipient)?.Balance ?? UInt256.Zero;
        Block? block = await blockProductionTrigger.BuildBlock(parentHeader, cancellationToken, NullBlockTracer.Instance, payloadAttributes);
        if (block is not null)
        {
            CurrentBestBlock = block;
            UInt256 balanceAfter = _stateReader.GetAccount(block.StateRoot!, payloadAttributes.SuggestedFeeRecipient)?.Balance ?? UInt256.Zero;
            await _boostRelay.SendPayload(new BoostExecutionPayloadV1 { Block = new ExecutionPayloadV1(block), Profit = balanceAfter - balanceBefore }, cancellationToken);
        }

        return CurrentBestBlock;
    }

    public Task<Block?> ImprovementTask { get; }

    public Block? CurrentBestBlock { get; private set; }

    public void Dispose()
    {
        CancellationTokenExtensions.CancelDisposeAndClear(ref _cancellationTokenSource);
    }
}
