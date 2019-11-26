//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class TransactionInfo
    {
        public Keccak Hash { get; private set; }
        public UInt256 Value { get; private set; }
        public UInt256 GasPrice { get; private set; }
        public ulong Timestamp { get; private set; }

        public TransactionInfo(Keccak hash, UInt256 value, UInt256 gasPrice, ulong timestamp)
        {
            Hash = hash;
            Value = value;
            GasPrice = gasPrice;
            Timestamp = timestamp;
        }
    }
}