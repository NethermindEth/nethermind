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
