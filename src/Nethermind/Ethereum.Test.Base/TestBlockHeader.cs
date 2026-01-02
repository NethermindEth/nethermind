// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Ethereum.Test.Base
{
    public class TestBlockHeader
    {
        public Bloom Bloom { get; set; }
        public Address Coinbase { get; set; }
        public BigInteger Difficulty { get; set; }
        public byte[] ExtraData { get; set; }
        public BigInteger GasLimit { get; set; }
        public BigInteger GasUsed { get; set; }
        public Hash256 Hash { get; set; }
        public Hash256 MixHash { get; set; }
        public BigInteger Nonce { get; set; }
        public BigInteger Number { get; set; }
        public Hash256 ParentHash { get; set; }
        public Hash256 ReceiptTrie { get; set; }
        public Hash256 StateRoot { get; set; }
        public BigInteger Timestamp { get; set; }
        public Hash256 TransactionsTrie { get; set; }
        public Hash256 UncleHash { get; set; }
    }
}
