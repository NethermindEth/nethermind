// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus
{
    public class TargetAdjustedGasLimitCalculator(ISpecProvider? specProvider, IBlocksConfig? blocksConfig) : IGasLimitCalculator
    {
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly IBlocksConfig _blocksConfig = blocksConfig ?? throw new ArgumentNullException(nameof(blocksConfig));

        public ulong GetGasLimit(BlockHeader parentHeader)
        {
            ulong parentGasLimit = parentHeader.GasLimit;
            ulong gasLimit = parentGasLimit;

            ulong? targetGasLimit = _blocksConfig.TargetBlockGasLimit;
            ulong newBlockNumber = parentHeader.Number + 1;
            IReleaseSpec spec = _specProvider.GetSpec(newBlockNumber, parentHeader.Timestamp); // taking the parent timestamp is a temporary solution
            if (targetGasLimit is not null)
            {
                ulong target = targetGasLimit.Value;
                ulong div = parentGasLimit / spec.GasLimitBoundDivisor;
                ulong maxGasLimitDifference = div.SaturatingSub(1);
                gasLimit = target > parentGasLimit
                    ? parentGasLimit + Math.Min(target - parentGasLimit, maxGasLimitDifference)
                    : parentGasLimit - Math.Min(parentGasLimit - target, maxGasLimitDifference);
            }

            gasLimit = Eip1559GasLimitAdjuster.AdjustGasLimit(spec, gasLimit, newBlockNumber);
            return Math.Max(gasLimit, spec.MinGasLimit);
        }
    }
}
