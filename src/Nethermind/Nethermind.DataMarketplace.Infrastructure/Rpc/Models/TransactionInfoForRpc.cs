// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
