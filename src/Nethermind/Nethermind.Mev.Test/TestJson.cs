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

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Mev.Test
{
    public class TestJson : ICloneable
    {
        public string? Description { get; set; }
        
        public TxForTest?[]? Txs { get; set; }
        
        public MevBundleForTest?[]? Bundles { get; set; }
        
        public UInt256? OptimalProfit { get; set; }

        public long? GasLimit { get; set; }

        public SelectorType SelectorType { get; set; }
        
        public TailGasType TailGasType { get; set; }

        public long MinOptimalProfitRatio { get; set; } = 100;

        public long MaxGasLimitRatio { get; set; } = 100;
        
        public object Clone()
        {
            TestJson testJson = new ();
            testJson.Description = Description;
            testJson.Bundles = Bundles;
            testJson.Txs = Txs;
            testJson.OptimalProfit = OptimalProfit;
            testJson.GasLimit = GasLimit;
            testJson.SelectorType = SelectorType;
            testJson.MinOptimalProfitRatio = MinOptimalProfitRatio;
            return testJson;
        }

        public override string ToString()
        {
            return $"{Description} {SelectorType} tail {TailGasType} gas {MaxGasLimitRatio}% profit {MinOptimalProfitRatio}%";
        }
    }
}
