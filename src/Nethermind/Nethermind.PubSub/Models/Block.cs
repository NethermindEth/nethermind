/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

namespace Nethermind.PubSub.Models
{
    public class Block
    {
        public BlockHeader Header { get; set; }
        public Transaction[] Transactions { get; set; }
        public BlockHeader[] Ommers { get; set; }
        public byte[] Hash { get; set; }
        public byte[] ParentHash { get; set; }
        public byte[] Beneficiary { get; set; }
        public byte[] StateRoot { get; set; }
        public byte[] TransactionsRoot { get; set; }
        public long GasLimit { get; set; }
        public long GasUsed { get; set; }
        public string Timestamp { get; set; }
        public string Number { get; set; }
        public string Difficulty { get; set; }
        public string TotalDifficulty { get; set; }
        public string TotalTransactions { get; set; }
    }
}