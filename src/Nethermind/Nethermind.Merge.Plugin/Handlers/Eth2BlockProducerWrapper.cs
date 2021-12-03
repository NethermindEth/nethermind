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

using System.Threading.Tasks;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public class Eth2BlockProducerWrapper : IConsensusBlockProducer
{
    private readonly Eth2BlockProducerFactory _eth2BlockProducerFactory;
    private readonly Eth2BlockProductionContext _eth2BlockProductionContext;

    public Eth2BlockProducerWrapper(
        Eth2BlockProducerFactory eth2BlockProducerFactory,
        Eth2BlockProductionContext eth2BlockProductionContext
        )
    {
        _eth2BlockProducerFactory = eth2BlockProducerFactory;
        _eth2BlockProductionContext = eth2BlockProductionContext;
        
        DefaultBlockProductionTrigger = eth2BlockProductionContext.BlockProductionTrigger;
    }

    public async Task<IBlockProducer> InitBlockProducer(
        IBlockProductionTrigger? blockProductionTrigger = null,
        ITxSource? additionalTxSource = null)
    {
        BlockProducerEnv producerEnv = _eth2BlockProductionContext.BlockProducerEnv;
        
        return _eth2BlockProducerFactory.Create(_eth2BlockProductionContext, producerEnv.TxSource.Then(additionalTxSource), blockProductionTrigger);
    }
    
    public IBlockProductionTrigger DefaultBlockProductionTrigger { get; }
}
