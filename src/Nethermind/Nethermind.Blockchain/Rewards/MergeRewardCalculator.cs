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
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Rewards
{
    public class MergeRewardCalculator : IRewardCalculator, IRewardCalculatorSource
    {
        private readonly IRewardCalculator _preMergeRewardCalculator;
        private readonly ISpecProvider _specProvider;
        
        public MergeRewardCalculator(IRewardCalculator preMergeRewardCalculator, ISpecProvider specProvider)
        {
            _preMergeRewardCalculator = preMergeRewardCalculator;
            _specProvider = specProvider;
        }
        public BlockReward[] CalculateRewards(Block block)
        {
            return _specProvider.GetSpec(block.Number).IsEip3675Enabled
                ? Array.Empty<BlockReward>()
                : _preMergeRewardCalculator.CalculateRewards(block);
        }

        public IRewardCalculator Get(ITransactionProcessor processor) => this;
    }
}
