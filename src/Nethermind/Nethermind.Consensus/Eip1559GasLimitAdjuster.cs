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
    public class Eip1559GasLimitAdjuster : IEip1559GasLimitAdjuster
    {       
        private readonly ISpecProvider _specProvider;
        
        public Eip1559GasLimitAdjuster(ISpecProvider specProvider) 
        {
            _specProvider = specProvider;
        }

        public long GetGasLimit(BlockHeader blockHeader)
        {
            long gaslimit = blockHeader.GasLimit;
            if (_specProvider.GetSpec(blockHeader.Number).IsEip1559Enabled)
            {
                gaslimit *= Eip1559Constants.ElasticityMultiplier;
            }

            return gaslimit;
        }

        public long AdjustGasLimit(long blockNumber, long gasLimit)
        {
            long adjustedGasLimit = gasLimit;
            if (_specProvider.GetSpec(blockNumber).IsEip1559Enabled)
            {
                adjustedGasLimit *= Eip1559Constants.ElasticityMultiplier;
            }

            return adjustedGasLimit;
        }
    }
}
