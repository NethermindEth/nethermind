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
using System.Linq;
using MathGmp.Native;
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
        public TransactionForRpc(Transaction transaction) : this(null, null, null, transaction) { }

        public TransactionForRpc(Keccak? blockHash, long? blockNumber, int? txIndex, Transaction transaction, UInt256? baseFee = null)
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
            if (transaction.IsEip1559)
            {
                GasPrice = baseFee != null
                    ? transaction.CalculateEffectiveGasPrice(true, baseFee.Value)
                    : transaction.MaxFeePerGas;
                MaxFeePerGas = transaction.MaxFeePerGas;
                MaxPriorityFeePerGas = transaction.MaxPriorityFeePerGas;
            }
            ChainId = transaction.ChainId;
            Type = transaction.Type;
            AccessList = transaction.AccessList is null ? null : AccessListItemForRpc.FromAccessList(transaction.AccessList);

            Signature? signature = transaction.Signature;
            if (signature != null)
            {
                R = new UInt256(signature.R, true);
                S = new UInt256(signature.S, true);
                V = transaction.Type == TxType.Legacy ? (UInt256?)signature.V : (UInt256?)signature.RecoveryId;
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
        
        public UInt256? MaxPriorityFeePerGas { get; set; }
        
        public UInt256? MaxFeePerGas { get; set; }
        public long? Gas { get; set; }
        public byte[]? Data { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public byte[]? Input { get; set; }
        
        public UInt256? ChainId { get; set; }
        
        public TxType Type { get; set; }
        
        public AccessListItemForRpc[]? AccessList { get; set; }

        public UInt256? V { get; set; }

        public UInt256? S { get; set; }

        public UInt256? R { get; set; }

        public Transaction ToTransactionWithDefaults(ulong? chainId = null) => ToTransactionWithDefaults<Transaction>(chainId);

        public T ToTransactionWithDefaults<T>(ulong? chainId = null) where T : Transaction, new()
        {
            T tx = new()
            {
                GasLimit = Gas ?? 90000,
                GasPrice = GasPrice ?? 20.GWei(),
                Nonce = (ulong)(Nonce ?? 0), // here pick the last nonce?
                To = To,
                SenderAddress = From,
                Value = Value ?? 0,
                Data = Data ?? Input,
                Type = Type,
                AccessList = TryGetAccessList(),
                ChainId = chainId,
                DecodedMaxFeePerGas = MaxFeePerGas ?? 0
            };

            if (tx.IsEip1559)
            {
                tx.GasPrice = MaxPriorityFeePerGas ?? 0;
            }

            return tx;
        }

        public Transaction ToTransaction(ulong? chainId = null) => ToTransaction<Transaction>();

        public T ToTransaction<T>(ulong? chainId = null) where T : Transaction, new()
        {
            T tx = new()
            {
                GasLimit = Gas ?? 0,
                GasPrice = GasPrice ?? 0,
                Nonce = (ulong)(Nonce ?? 0), // here pick the last nonce?
                To = To,
                SenderAddress = From,
                Value = Value ?? 0,
                Data = Data ?? Input,
                Type = Type,
                AccessList = TryGetAccessList(),
                ChainId = chainId
            };
            
            if (tx.IsEip1559)
            {
                tx.GasPrice = MaxPriorityFeePerGas ?? 0;
                tx.DecodedMaxFeePerGas = MaxFeePerGas ?? 0;
            }

            return tx;
        }

        private AccessList? TryGetAccessList() =>
            Type != TxType.AccessList || AccessList == null 
                ? null 
                : AccessListItemForRpc.ToAccessList(AccessList);
    }
}
