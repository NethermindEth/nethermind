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

using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.Data
{
    public class Eth2BlockProductionContext
    {
        public void Init(IBlockProducerEnvFactory blockProducerEnvFactory, ITxSource? additionalTxSource = null)
        {
            BlockProductionTrigger = new BuildBlocksWhenRequested();
            BlockProducerEnv = blockProducerEnvFactory.Create(additionalTxSource);
        }
        
        public IManualBlockProductionTrigger BlockProductionTrigger { get; set; }
        
        public BlockProducerEnv BlockProducerEnv { get; set; }
        
        public Eth2BlockProducer BlockProducer { get; set; }
    }
}
