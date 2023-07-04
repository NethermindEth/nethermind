// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
        public Keccak Hash { get; set; }
        public Keccak MixHash { get; set; }
        public BigInteger Nonce { get; set; }
        public BigInteger Number { get; set; }
        public Keccak ParentHash { get; set; }
        public Keccak ReceiptTrie { get; set; }
        public Keccak StateRoot { get; set; }
        public BigInteger Timestamp { get; set; }
        public Keccak TransactionsTrie { get; set; }
        public Keccak UncleHash { get; set; }
    }
}
