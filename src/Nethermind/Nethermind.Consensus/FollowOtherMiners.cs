// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus
{
    public class FollowOtherMiners : IGasLimitCalculator
    {
        private readonly ISpecProvider _specProvider;

        public FollowOtherMiners(ISpecProvider specProvider)
        {
            _specProvider = specProvider;
        }

        public long GetGasLimit(BlockHeader parentHeader, long? targetGasLimit = null)
        {
            if (targetGasLimit is not null)
                throw new InvalidOperationException("You cannot set target gas limit for FollowOtherMiners");
            long gasLimit = parentHeader.GasLimit;
            long newBlockNumber = parentHeader.Number + 1;
            IReleaseSpec spec = _specProvider.GetSpec(parentHeader);
            gasLimit = Eip1559GasLimitAdjuster.AdjustGasLimit(spec, gasLimit, newBlockNumber);
            return gasLimit;
        }

    }
}
