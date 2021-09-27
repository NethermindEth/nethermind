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
using Nethermind.Blockchain.Rewards;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Merge.Plugin
{
    public class MergeRewardCalculator : IRewardCalculator, IRewardCalculatorSource
    {
        private readonly IRewardCalculator _preMergeRewardCalculator;
        private readonly IPoSSwitcher _poSSwitcher;

        public MergeRewardCalculator(IRewardCalculator preMergeRewardCalculator, IPoSSwitcher poSSwitcher)
        {
            _preMergeRewardCalculator = preMergeRewardCalculator;
            _poSSwitcher = poSSwitcher;
        }

        public BlockReward[] CalculateRewards(Block block)
        {
            return _poSSwitcher.IsPos(block.Header)
                ? Array.Empty<BlockReward>()
                : _preMergeRewardCalculator.CalculateRewards(block);
        }

        public IRewardCalculator Get(ITransactionProcessor processor) => this;
    }
}
