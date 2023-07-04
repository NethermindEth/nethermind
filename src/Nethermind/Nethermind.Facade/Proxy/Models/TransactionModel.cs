// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models
{
    public class TransactionModel
    {
        public Keccak Hash { get; set; }
        public UInt256 Nonce { get; set; }
        public Keccak BlockHash { get; set; }
        public UInt256 BlockNumber { get; set; }
        public Address From { get; set; }
        public Address To { get; set; }
        public UInt256 Gas { get; set; }
        public UInt256 GasPrice { get; set; }
        public byte[] Input { get; set; }
        public UInt256 Value { get; set; }

        public Transaction ToTransaction()
            => new()
            {
                Hash = Hash,
                Nonce = Nonce,
                SenderAddress = From,
                To = To,
                Data = Input,
                Value = Value,
                GasLimit = (long)Gas,
                GasPrice = GasPrice
            };
    }
}
