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
using Nethermind.Int256;

namespace Nethermind.Mev
{
    public class MevBlockProducer : MultipleBlockProducer<MevBlockProducer.MevBlockProducerInfo>
    {
        public MevBlockProducer(IBlockProductionTrigger blockProductionTrigger, params MevBlockProducerInfo[] blockProducers) 
            : base(blockProductionTrigger, new MevBestBlockPicker(), blockProducers)
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
                        var beneficiaryBalanceSource = context.BlockProducerInfo.BeneficiaryBalanceSource;
                        UInt256 balance = beneficiaryBalanceSource.BeneficiaryBalance;
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
            public IBeneficiaryBalanceSource BeneficiaryBalanceSource { get; }
            public MevBlockProducerInfo(
                IBlockProducer blockProducer, 
                IManualBlockProductionTrigger blockProductionTrigger, 
                IBeneficiaryBalanceSource beneficiaryBalanceSource)
            {
                BlockProducer = blockProducer;
                BlockProductionTrigger = blockProductionTrigger;
                BeneficiaryBalanceSource = beneficiaryBalanceSource;
            }
        }
    }
}
