// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostBlockImprovementContextFactory : IBlockImprovementContextFactory
{
    private readonly IBlockProducer _blockProducer;
    private readonly TimeSpan _timeout;
    private readonly IBoostRelay _boostRelay;
    private readonly IStateReader _stateReader;

    public BoostBlockImprovementContextFactory(IBlockProducer blockProducer, TimeSpan timeout, IBoostRelay boostRelay, IStateReader stateReader)
    {
        _blockProducer = blockProducer;
        _timeout = timeout;
        _boostRelay = boostRelay;
        _stateReader = stateReader;
    }

    public IBlockImprovementContext StartBlockImprovementContext(
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        UInt256 currentBlockFees,
        CancellationTokenSource cts) =>
        new BoostBlockImprovementContext(currentBestBlock, _blockProducer, _timeout, parentHeader, payloadAttributes, _boostRelay, _stateReader, startDateTime, cts);
}
