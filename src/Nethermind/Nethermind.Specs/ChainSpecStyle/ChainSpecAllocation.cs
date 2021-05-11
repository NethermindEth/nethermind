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

using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle
{
    public class ChainSpecAllocation
    {
        public ChainSpecAllocation()
        {
        }
        
        public ChainSpecAllocation(UInt256 allocationValue)
        {
            Balance = allocationValue;
        }

        public ChainSpecAllocation(UInt256 allocationValue, UInt256 nonce, byte[] code, byte[] constructor, Dictionary<UInt256, byte[]> storage)
        {
            Balance = allocationValue;
            Nonce = nonce;
            Code = code;
            Constructor = constructor;
            Storage = storage;
        }
        
        public UInt256 Balance { get; set; }
        
        public UInt256 Nonce { get; set; }
        
        public byte[] Code { get; set; }
        
        public byte[] Constructor { get; set; }

        public Dictionary<UInt256, byte[]> Storage { get; set; }

    }
}
