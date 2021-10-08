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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class EthRequest
    {
        public Keccak Id { get; private set; }
        public string Host { get; private set; }
        public Address Address { get; private set; }
        public UInt256 Value { get; private set; }
        public DateTime RequestedAt { get; private set; }
        public Keccak TransactionHash { get; private set; }

        public EthRequest(Keccak id, string host, Address address, UInt256 value, DateTime requestedAt,
            Keccak transactionHash)
        {
            Id = id;
            Host = host;
            Address = address;
            Value = value;
            RequestedAt = requestedAt;
            TransactionHash = transactionHash;
        }

        public void UpdateRequestDetails(Address address, UInt256 value, DateTime requestedAt, Keccak transactionHash)
        {
            Address = address;
            Value = value;
            RequestedAt = requestedAt;
            TransactionHash = transactionHash;
        }
    }
}