// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Consensus.Rewards;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Merge.Plugin
{
    public class MergeRewardCalculatorSource : IRewardCalculatorSource
    {
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IRewardCalculatorSource _beforeTheMerge;

        public MergeRewardCalculatorSource(
            IRewardCalculatorSource? beforeTheMerge,
            IPoSSwitcher poSSwitcher)
        {
            _beforeTheMerge = beforeTheMerge ?? throw new ArgumentNullException(nameof(beforeTheMerge));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        }

        public IRewardCalculator Get(ITransactionProcessor processor)
        {
            return new MergeRewardCalculator(_beforeTheMerge.Get(processor), _poSSwitcher);
        }
    }
}
