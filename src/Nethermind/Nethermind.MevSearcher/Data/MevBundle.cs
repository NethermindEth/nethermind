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

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.MevSearcher.Data
{
    public class MevBundle
    {
        public MevBundle(long blockNumber, Transaction[] transactions, Keccak[] revertingTxHashes, UInt256? minTimestamp = null, UInt256? maxTimestamp = null)
        {
            BlockNumber = blockNumber;
            Transactions = transactions;
            RevertingTxHashes = revertingTxHashes;

            MinTimestamp = minTimestamp ?? UInt256.Zero;
            MaxTimestamp = maxTimestamp ?? UInt256.Zero;
        }
        
        public Transaction[] Transactions { get; }
        
        public Keccak[] RevertingTxHashes { get; }

        public long BlockNumber { get; }
        
        public UInt256 MaxTimestamp { get; }
        
        public UInt256 MinTimestamp { get; }
    }
}
