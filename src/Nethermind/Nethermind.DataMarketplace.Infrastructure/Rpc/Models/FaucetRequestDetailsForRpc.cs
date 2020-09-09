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
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class FaucetRequestDetailsForRpc
    {
        public string? Host { get; set; }
        public Address? Address { get; set; }
        public UInt256? Value { get; set; }
        public DateTime? Date { get; set; }
        public Keccak? TransactionHash { get; set; }

        public FaucetRequestDetailsForRpc()
        {
        }

        public FaucetRequestDetailsForRpc(FaucetRequestDetails request)
            : this(request.Host, request.Address, request.Value, request.Date, request.TransactionHash)
        {
        }

        public FaucetRequestDetailsForRpc(string? host, Address? address, UInt256? value, DateTime? date, Keccak? transactionHash)
        {
            Host = host;
            Address = address;
            Value = value;
            Date = date;
            TransactionHash = transactionHash;
        }
    }
}