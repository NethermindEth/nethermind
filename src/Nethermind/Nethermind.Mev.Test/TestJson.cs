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
        public string? Name { get; set; }
        
        public string? Description { get; set; }
        
        public TxForTest?[]? Txs { get; set; }
        
        public MevBundleForTest?[]? Bundles { get; set; }
        
        public UInt256? OptimalProfitV1 { get; set; }
        
        public UInt256? OptimalProfitV2_max1bundles { get; set; }
        
        public UInt256? OptimalProfitV2_max3bundles { get; set; }

        public long? GasLimit { get; set; }

        public SelectorType SelectorType { get; set; }
        
        public int MaxMergedBundles { get; set; } = 5;
        
        public object Clone()
        {
            TestJson testJson = new ();
            testJson.Name = Name;
            testJson.Description = Description;
            testJson.Bundles = Bundles;
            testJson.Txs = Txs;
            testJson.OptimalProfitV1 = OptimalProfitV1;
            testJson.OptimalProfitV2_max1bundles = OptimalProfitV2_max1bundles;
            testJson.OptimalProfitV2_max3bundles = OptimalProfitV2_max3bundles;
            testJson.GasLimit = GasLimit;
            testJson.SelectorType = SelectorType;
            testJson.MaxMergedBundles = MaxMergedBundles;
            return testJson;
        }

        public override string ToString()
        {
            return $"{Name} {SelectorType} maxbundles:{MaxMergedBundles}";
        }
    }
}
