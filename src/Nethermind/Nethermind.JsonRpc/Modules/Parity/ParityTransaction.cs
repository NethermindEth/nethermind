// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Evm;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityTransaction
    {
        public Hash256 Hash { get; set; }
        public UInt256? Nonce { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Hash256 BlockHash { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public UInt256? BlockNumber { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public UInt256? TransactionIndex { get; set; }
        public Address From { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Address To { get; set; }
        public UInt256? Value { get; set; }
        public UInt256? GasPrice { get; set; }
        public long? Gas { get; set; }
        public byte[] Input { get; set; }
        public byte[] Raw { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Address Creates { get; set; }
        public PublicKey PublicKey { get; set; }
        public ulong? ChainId { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public object Condition { get; set; }
        public Memory<byte> R { get; set; }
        public Memory<byte> S { get; set; }
        public UInt256 V { get; set; }
        public UInt256 StandardV { get; set; }

        public ParityTransaction()
        {
        }

        public ParityTransaction(Transaction transaction, byte[] raw, PublicKey publicKey,
            Hash256 blockHash = null, UInt256? blockNumber = null, UInt256? txIndex = null)
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
            Input = transaction.Data.AsArray();
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
