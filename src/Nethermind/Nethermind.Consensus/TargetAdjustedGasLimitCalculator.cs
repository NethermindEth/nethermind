// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus
{
    public class TargetAdjustedGasLimitCalculator : IGasLimitCalculator
    {
        private readonly ISpecProvider _specProvider;
        private readonly IBlocksConfig _blocksConfig;

        public TargetAdjustedGasLimitCalculator(ISpecProvider? specProvider, IBlocksConfig? miningConfig)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blocksConfig = miningConfig ?? throw new ArgumentNullException(nameof(miningConfig));
        }

        public long GetGasLimit(BlockHeader parentHeader)
        {
            long parentGasLimit = parentHeader.GasLimit;
            long gasLimit = parentGasLimit;

            long? targetGasLimit = _blocksConfig.TargetBlockGasLimit;
            long newBlockNumber = parentHeader.Number + 1;
            IReleaseSpec spec = _specProvider.GetSpec(newBlockNumber, parentHeader.Timestamp); // taking the parent timestamp is a temprory solution
            if (targetGasLimit is not null)
            {
                long maxGasLimitDifference = Math.Max(0, parentGasLimit / spec.GasLimitBoundDivisor - 1);
                gasLimit = targetGasLimit.Value > parentGasLimit
                    ? parentGasLimit + Math.Min(targetGasLimit.Value - parentGasLimit, maxGasLimitDifference)
                    : parentGasLimit - Math.Min(parentGasLimit - targetGasLimit.Value, maxGasLimitDifference);
            }

            gasLimit = Eip1559GasLimitAdjuster.AdjustGasLimit(spec, gasLimit, newBlockNumber);
            return gasLimit;
        }
    }
}
