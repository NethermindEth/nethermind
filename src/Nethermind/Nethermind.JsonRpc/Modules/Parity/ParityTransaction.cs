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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Evm;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityTransaction
    {
        public Keccak Hash { get; set; }
        public UInt256? Nonce { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Keccak BlockHash { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public UInt256? BlockNumber { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public UInt256? TransactionIndex { get; set; }
        public Address From { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Address To { get; set; }
        public UInt256? Value { get; set; }
        public UInt256? GasPrice { get; set; }
        public long? Gas { get; set; }
        public byte[] Input { get; set; }
        public byte[] Raw { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Address Creates { get; set; }
        public PublicKey PublicKey { get; set; }
        public ulong? ChainId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public object Condition { get; set; }
        public byte[] R { get; set; }
        public byte[] S { get; set; }
        public UInt256 V { get; set; }
        public UInt256 StandardV { get; set; }

        public ParityTransaction()
        {
        }

        public ParityTransaction(Transaction transaction, byte[] raw, PublicKey publicKey,
            Keccak blockHash = null, UInt256? blockNumber = null, UInt256? txIndex = null)
        {
            Hash = transaction.Hash;
            Nonce = transaction.Nonce;
            BlockHash = blockHash;
            BlockNumber = blockNumber;
            TransactionIndex = txIndex;
            From = transaction.SenderAddress;
            To = transaction.IsContractCreation ? null : transaction.To;
            Value = transaction.Value;
            GasPrice = transaction.GasPrice;
            Gas = transaction.GasLimit;
            Raw = raw;
            Input = transaction.Data;
            PublicKey = publicKey;
            ChainId = transaction.Signature.ChainId;
            R = transaction.Signature.R;
            S = transaction.Signature.S;
            V = (UInt256)transaction.Signature.V;
            StandardV = transaction.Signature.RecoveryId;
            // TKS: it does not seem to work with CREATE2
            Creates = transaction.IsContractCreation ? ContractAddress.From(transaction.SenderAddress, transaction.Nonce) : null;
        }
    }
}
