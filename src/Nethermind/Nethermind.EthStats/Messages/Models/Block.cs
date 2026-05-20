// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.EthStats.Messages.Models
{
    public class Block(long number, string hash, string parentHash, long timestamp, string miner, long gasUsed,
        long gasLimit, string difficulty, string totalDifficulty, IEnumerable<Transaction> transactions,
        string transactionsRoot, string stateRoot, IEnumerable<Uncle> uncles)
    {
        public long Number { get; } = number;
        public string Hash { get; } = hash;
        public string ParentHash { get; } = parentHash;
        public long Timestamp { get; } = timestamp;
        public string Miner { get; } = miner;
        public long GasUsed { get; } = gasUsed;
        public long GasLimit { get; } = gasLimit;
        public string Difficulty { get; } = difficulty;
        public string TotalDifficulty { get; } = totalDifficulty;
        public IEnumerable<Transaction> Transactions { get; } = transactions;
        public string TransactionsRoot { get; } = transactionsRoot;
        public string StateRoot { get; } = stateRoot;
        public IEnumerable<Uncle> Uncles { get; } = uncles;
    }

    public class Transaction(string hash)
    {
        public string Hash { get; } = hash;
    }

    public class Uncle
    {
    }
}
