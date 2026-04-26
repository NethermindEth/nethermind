// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostBlockImprovementContextFactory(IBlockProducer blockProducer, TimeSpan timeout, IBoostRelay boostRelay, IStateReader stateReader) : IBlockImprovementContextFactory
{
    private readonly IBlockProducer _blockProducer = blockProducer;
    private readonly TimeSpan _timeout = timeout;
    private readonly IBoostRelay _boostRelay = boostRelay;
    private readonly IStateReader _stateReader = stateReader;

    public IBlockImprovementContext StartBlockImprovementContext(
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        UInt256 currentBlockFees,
        SharedCancellationTokenSource cts) =>
        new BoostBlockImprovementContext(currentBestBlock, _blockProducer, _timeout, parentHeader, payloadAttributes, _boostRelay, _stateReader, startDateTime, cts);
}
