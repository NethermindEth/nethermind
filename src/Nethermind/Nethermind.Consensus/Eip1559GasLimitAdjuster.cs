// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus
{
    /// <summary>In the 1559 fork block the new gas limit is gasLimit * Eip1559Constants.ElasticityMultiplier.</summary>
    public static class Eip1559GasLimitAdjuster
    {
        public static long AdjustGasLimit(IReleaseSpec releaseSpec, long gasLimit, long blockNumber)
        {
            long adjustedGasLimit = gasLimit;
            if (releaseSpec.Eip1559TransitionBlock == blockNumber)
            {
                adjustedGasLimit *= Eip1559Constants.ElasticityMultiplier;
            }

            return adjustedGasLimit;
        }
    }
}
