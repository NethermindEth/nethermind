// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Consensus.Rewards;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Merge.Plugin
{
    public class MergeRewardCalculatorSource(
        IRewardCalculatorSource? beforeTheMerge,
        IPoSSwitcher poSSwitcher) : IRewardCalculatorSource
    {
        private readonly IPoSSwitcher _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        private readonly IRewardCalculatorSource _beforeTheMerge = beforeTheMerge ?? throw new ArgumentNullException(nameof(beforeTheMerge));

        public IRewardCalculator Get(ITransactionProcessor processor) => new MergeRewardCalculator(_beforeTheMerge.Get(processor), _poSSwitcher);
    }
}
