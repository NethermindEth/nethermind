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

using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public class Eip1559Constants
    {
                
        public static readonly UInt256 DefaultForkBaseFee = 1.GWei();
        
        public static readonly UInt256 DefaultBaseFeeMaxChangeDenominator = 8;
        
        public static readonly int DefaultElasticityMultiplier = 2;
        
        // The above values are the default ones. However, we're allowing to override it from genesis
        public static UInt256 ForkBaseFee { get; set; } = DefaultForkBaseFee;
        public static UInt256 BaseFeeMaxChangeDenominator { get; set; } = DefaultBaseFeeMaxChangeDenominator;
        public static long ElasticityMultiplier { get; set;  } = DefaultElasticityMultiplier;
    }
}
