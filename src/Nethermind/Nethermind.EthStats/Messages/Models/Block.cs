// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.EthStats.Messages.Models
{
    public class Block
    {
        public long Number { get; }
        public string Hash { get; }
        public string ParentHash { get; }
        public long Timestamp { get; }
        public string Miner { get; }
        public long GasUsed { get; }
        public long GasLimit { get; }
        public string Difficulty { get; }
        public string TotalDifficulty { get; }
        public IEnumerable<Transaction> Transactions { get; }
        public string TransactionsRoot { get; }
        public string StateRoot { get; }
        public IEnumerable<Uncle> Uncles { get; }

        public Block(long number, string hash, string parentHash, long timestamp, string miner, long gasUsed,
            long gasLimit, string difficulty, string totalDifficulty, IEnumerable<Transaction> transactions,
            string transactionsRoot, string stateRoot, IEnumerable<Uncle> uncles)
        {
            Number = number;
            Hash = hash;
            ParentHash = parentHash;
            Timestamp = timestamp;
            Miner = miner;
            GasUsed = gasUsed;
            GasLimit = gasLimit;
            Difficulty = difficulty;
            TotalDifficulty = totalDifficulty;
            Transactions = transactions;
            TransactionsRoot = transactionsRoot;
            StateRoot = stateRoot;
            Uncles = uncles;
        }
    }

    public class Transaction
    {
        public string Hash { get; }

        public Transaction(string hash)
        {
            Hash = hash;
        }
    }

    public class Uncle
    {
    }
}
