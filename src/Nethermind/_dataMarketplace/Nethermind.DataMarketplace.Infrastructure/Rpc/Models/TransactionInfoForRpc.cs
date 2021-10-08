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
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class TransactionInfoForRpc
    {
        public Keccak? Hash { get; set; }
        public UInt256? Value { get; set; }
        public UInt256? GasPrice { get; set; }
        public ulong? GasLimit { get; set; }
        public UInt256? MaxFee => GasPrice * GasLimit;
        public ulong? Timestamp { get; set; }
        public string? Type { get; set; }
        public string? State { get; set; }

        public TransactionInfoForRpc()
        {
        }

        public TransactionInfoForRpc(TransactionInfo transaction) :
            this(transaction.Hash, transaction.Value, transaction.GasPrice, transaction.GasLimit, transaction.Timestamp,
                transaction.Type.ToString().ToLowerInvariant(), transaction.State.ToString().ToLowerInvariant())
        {
        }

        public TransactionInfoForRpc(Keccak? hash, UInt256 value, UInt256 gasPrice, ulong gasLimit, ulong timestamp,
            string type, string state)
        {
            Hash = hash;
            Value = value;
            GasPrice = gasPrice;
            GasLimit = gasLimit;
            Timestamp = timestamp;
            Type = type;
            State = state;
        }
    }
}