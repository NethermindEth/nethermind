// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus
{
    public class TargetAdjustedGasLimitCalculator(ISpecProvider? specProvider, IBlocksConfig? miningConfig) : IGasLimitCalculator
    {
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly IBlocksConfig _blocksConfig = miningConfig ?? throw new ArgumentNullException(nameof(miningConfig));

        public long GetGasLimit(BlockHeader parentHeader)
        {
            long parentGasLimit = parentHeader.GasLimit;
            long gasLimit = parentGasLimit;

            long? targetGasLimit = _blocksConfig.TargetBlockGasLimit;
            long newBlockNumber = parentHeader.Number + 1;
            IReleaseSpec spec = _specProvider.GetSpec(newBlockNumber, parentHeader.Timestamp); // taking the parent timestamp is a temporary solution
            if (targetGasLimit is not null)
            {
                long maxGasLimitDifference = Math.Max(0, parentGasLimit / spec.GasLimitBoundDivisor - 1);
                gasLimit = targetGasLimit.Value > parentGasLimit
                    ? parentGasLimit + Math.Min(targetGasLimit.Value - parentGasLimit, maxGasLimitDifference)
                    : parentGasLimit - Math.Min(parentGasLimit - targetGasLimit.Value, maxGasLimitDifference);
            }

            gasLimit = Eip1559GasLimitAdjuster.AdjustGasLimit(spec, gasLimit, newBlockNumber);
            return Math.Max(gasLimit, spec.MinGasLimit);
        }
    }
}
