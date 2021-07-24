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

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Rewards
{
    public class RewardCalculator : IRewardCalculator, IRewardCalculatorSource
    {
        private readonly ISpecProvider _specProvider;

        public RewardCalculator(ISpecProvider specProvider)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }
        
        private UInt256 GetBlockReward(Block block)
        {
            IReleaseSpec spec = _specProvider.GetSpec(block.Number);
            return spec.BlockReward;
        }
        
        public BlockReward[] CalculateRewards(Block block)
        {
            if (block.IsGenesis)
            {
                return Array.Empty<BlockReward>();
            }
            
            UInt256 blockReward = GetBlockReward(block);
            BlockReward[] rewards = new BlockReward[1 + block.Ommers.Length];

            BlockHeader blockHeader = block.Header;
            UInt256 mainReward = blockReward + (uint) block.Ommers.Length * (blockReward >> 5);
            rewards[0] = new BlockReward(blockHeader.Beneficiary, mainReward);

            for (int i = 0; i < block.Ommers.Length; i++)
            {
                UInt256 ommerReward = GetOmmerReward(blockReward, blockHeader, block.Ommers[i]);
                rewards[i + 1] = new BlockReward(block.Ommers[i].Beneficiary, ommerReward, BlockRewardType.Uncle);
            }

            return rewards;
        }

        private UInt256 GetOmmerReward(UInt256 blockReward, BlockHeader blockHeader, BlockHeader ommer)
        {
            return blockReward - ((uint) (blockHeader.Number - ommer.Number) * blockReward >> 3);
        }

        public IRewardCalculator Get(ITransactionProcessor processor) => this;
    }
}
