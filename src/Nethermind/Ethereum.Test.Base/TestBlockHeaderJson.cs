// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Test.Base
{
    public class TestBlockHeaderJson
    {
        public string Bloom { get; set; }
        public string Coinbase { get; set; }
        public string Difficulty { get; set; }
        public string ExtraData { get; set; }
        public string GasLimit { get; set; }
        public string GasUsed { get; set; }
        public string Hash { get; set; }
        public string MixHash { get; set; }
        public string Nonce { get; set; }
        public string Number { get; set; }
        public string ParentHash { get; set; }
        public string ReceiptTrie { get; set; }
        public string StateRoot { get; set; }
        public string Timestamp { get; set; }
        public string TransactionsTrie { get; set; }
        public string UncleHash { get; set; }
    }
}
