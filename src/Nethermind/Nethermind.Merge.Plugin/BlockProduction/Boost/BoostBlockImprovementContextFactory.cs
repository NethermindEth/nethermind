// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Facade.Proxy;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostBlockImprovementContextFactory : IBlockImprovementContextFactory
{
    private readonly IManualBlockProductionTrigger _blockProductionTrigger;
    private readonly TimeSpan _timeout;
    private readonly IBoostRelay _boostRelay;
    private readonly IStateReader _stateReader;

    public BoostBlockImprovementContextFactory(IManualBlockProductionTrigger blockProductionTrigger, TimeSpan timeout, IBoostRelay boostRelay, IStateReader stateReader)
    {
        _blockProductionTrigger = blockProductionTrigger;
        _timeout = timeout;
        _boostRelay = boostRelay;
        _stateReader = stateReader;
    }

    public IBlockImprovementContext StartBlockImprovementContext(
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime) =>
        new BoostBlockImprovementContext(currentBestBlock, _blockProductionTrigger, _timeout, parentHeader, payloadAttributes, _boostRelay, _stateReader, startDateTime);
}
