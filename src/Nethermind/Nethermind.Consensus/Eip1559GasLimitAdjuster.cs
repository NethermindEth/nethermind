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
    /// <summary>Block.GasLimit in the context of EIP1559 transactions was renamed to GasTarget.
    /// The new GasLimit = ElasticityMultiplier * GasTarget </summary>
    public static class Eip1559GasLimitAdjuster
    {
        public static long AdjustGasLimit(bool isEip1559Enabled, long gasLimit)
        {
            long adjustedGasLimit = gasLimit;
            if (isEip1559Enabled)
            {
                adjustedGasLimit *= Eip1559Constants.ElasticityMultiplier;
            }

            return adjustedGasLimit;
        }
    }
}
