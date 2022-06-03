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
        PayloadAttributes payloadAttributes) =>
        new BoostBlockImprovementContext(currentBestBlock, _blockProductionTrigger, _timeout, parentHeader, payloadAttributes, _boostRelay, _stateReader);
}
