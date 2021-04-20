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
using System.Collections.Generic;
using System.Numerics;

namespace Nethermind.Mev
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class MevBundleForRpc
    {
        public List<Transaction> Transactions { get; set; }
        public BigInteger BlockNumber { get; set; } 
        public BigInteger MinTimestamp { get; set; }
        public BigInteger MaxTimestamp { get; set; }

        public MevBundleForRpc(List<Transaction> transactions, BigInteger blockNumber, BigInteger minTimestamp, BigInteger maxTimestamp) {
            (Transactions, BlockNumber, MinTimestamp, MaxTimestamp) = (transactions, blockNumber, minTimestamp, maxTimestamp);
        } 
    }
}
