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
        public Commitment Hash { get; set; }
        public Commitment MixHash { get; set; }
        public BigInteger Nonce { get; set; }
        public BigInteger Number { get; set; }
        public Commitment ParentHash { get; set; }
        public Commitment ReceiptTrie { get; set; }
        public Commitment StateRoot { get; set; }
        public BigInteger Timestamp { get; set; }
        public Commitment TransactionsTrie { get; set; }
        public Commitment UncleHash { get; set; }
    }
}
