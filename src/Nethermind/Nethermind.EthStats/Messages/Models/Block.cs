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
