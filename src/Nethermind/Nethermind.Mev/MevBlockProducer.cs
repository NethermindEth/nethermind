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

using System.Collections.Generic;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Mev
{
    public class MevBlockProducer : MultipleBlockProducer<MevBlockProducer.MevBlockProducerInfo>
    {
        public MevBlockProducer(IBlockProductionTrigger blockProductionTrigger, ILogManager logManager, params MevBlockProducerInfo[] blockProducers) 
            : base(blockProductionTrigger, new MevBestBlockPicker(), logManager, blockProducers)
        {
        }

        private class MevBestBlockPicker : IBestBlockPicker
        {
            public Block? GetBestBlock(IEnumerable<(Block? Block, MevBlockProducerInfo BlockProducerInfo)> blocks)
            {
                Block? best = null;
                UInt256 maxBalance = UInt256.Zero;
                foreach ((Block? Block, MevBlockProducerInfo BlockProducerInfo) context in blocks)
                {
                    if (context.Block is not null)
                    {
                        BeneficiaryTracer beneficiaryTracer = context.BlockProducerInfo.BeneficiaryTracer;
                        UInt256 balance = beneficiaryTracer.BeneficiaryBalance;
                        if (balance > maxBalance || best is null)
                        {
                            best = context.Block;
                            maxBalance = balance;
                        }
                    }
                }

                return best;
            }
        }

        public class MevBlockProducerInfo : IBlockProducerInfo
        {
            public IBlockProducer BlockProducer { get; }
            public IManualBlockProductionTrigger BlockProductionTrigger { get; }
            public IBlockTracer BlockTracer => BeneficiaryTracer;
            public BeneficiaryTracer BeneficiaryTracer { get; }
            public MevBlockProducerInfo(
                IBlockProducer blockProducer, 
                IManualBlockProductionTrigger blockProductionTrigger, 
                BeneficiaryTracer beneficiaryTracer)
            {
                BlockProducer = blockProducer;
                BlockProductionTrigger = blockProductionTrigger;
                BeneficiaryTracer = beneficiaryTracer;
            }
        }
    }
}
