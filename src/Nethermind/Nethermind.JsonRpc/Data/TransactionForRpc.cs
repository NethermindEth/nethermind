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

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Data
{
    public class TransactionForRpc
    {
        private readonly AccessListBuilder _accessListBuilder = new();

        public TransactionForRpc(Transaction transaction) : this(null, null, null, transaction) { }

        public TransactionForRpc(Keccak? blockHash, long? blockNumber, int? txIndex, Transaction transaction)
        {
            Hash = transaction.Hash;
            Nonce = transaction.Nonce;
            BlockHash = blockHash;
            BlockNumber = blockNumber;
            TransactionIndex = txIndex;
            From = transaction.SenderAddress;
            To = transaction.To;
            Value = transaction.Value;
            GasPrice = transaction.GasPrice;
            Gas = transaction.GasLimit;
            Input = Data = transaction.Data;
            Type = (UInt256)Convert.ToByte(transaction.Type);
            AccessList = transaction.AccessList?.Data.Select(i => new AccessListItemForRpc(i.Key, i.Value)).ToArray();

            Signature? signature = transaction.Signature;
            if (signature != null)
            {
                R = new UInt256(signature.R, true);
                S = new UInt256(signature.S, true);
                V = (UInt256?)signature.V;
            }
        }

        // ReSharper disable once UnusedMember.Global
        public TransactionForRpc()
        {
        }

        public Keccak? Hash { get; set; }
        public UInt256? Nonce { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Keccak? BlockHash { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public long? BlockNumber { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public long? TransactionIndex { get; set; }

        public Address? From { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Address? To { get; set; }

        public UInt256? Value { get; set; }
        public UInt256? GasPrice { get; set; }
        public long? Gas { get; set; }
        public byte[]? Data { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public byte[]? Input { get; set; }

        public UInt256 Type { get; set; }
        
        public AccessListItemForRpc[]? AccessList { get; set; }

        public UInt256? V { get; set; }

        public UInt256? S { get; set; }

        public UInt256? R { get; set; }

        public Transaction ToTransactionWithDefaults()
        {
            Transaction tx = new();
            tx.GasLimit = Gas ?? 90000;
            tx.GasPrice = GasPrice ?? 20.GWei();
            tx.Nonce = (ulong)(Nonce ?? 0); // here pick the last nonce?
            tx.To = To;
            tx.SenderAddress = From;
            tx.Value = Value ?? 0;
            tx.Data = Data ?? Input;
            tx.Type = (TxType)(byte)Type;
            tx.AccessList = TryGetAccessList();
            
            return tx;
        }

        public Transaction ToTransaction()
        {
            Transaction tx = new();
            tx.GasLimit = Gas ?? 0;
            tx.GasPrice = GasPrice ?? 0;
            tx.Nonce = (ulong)(Nonce ?? 0); // here pick the last nonce?
            tx.To = To;
            tx.SenderAddress = From;
            tx.Value = Value ?? 0;
            tx.Data = Data ?? Input;
            tx.Type = (TxType)(byte)Type;
            tx.AccessList = TryGetAccessList();

            return tx;
        }
        
        private AccessList? TryGetAccessList()
        {
            if (Type == 1 && AccessList != null)
            {
                foreach (var item in AccessList)
                {
                    _accessListBuilder.AddAddress(item.Address);
                    
                    foreach (var storageKey in item.StorageKeys)
                    {
                        _accessListBuilder.AddStorage(storageKey);
                    }
                }
                return _accessListBuilder.ToAccessList();
            }
            return null;
        }
    }
}
