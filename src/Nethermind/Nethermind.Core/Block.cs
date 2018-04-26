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

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Hash} ({Number})")]
    public class Block
    {
        public Block(BlockHeader blockHeader, IEnumerable<Transaction> transactions, IEnumerable<BlockHeader> ommers)
        {
            Header = blockHeader;
            Ommers = ommers.ToArray();
            Transactions = transactions.ToArray();
        }

        public Block(BlockHeader blockHeader, params BlockHeader[] ommers)
            : this(blockHeader, Enumerable.Empty<Transaction>(), ommers)
        {
        }

        public bool IsGenesis => Header.Number == 0;
        public BlockHeader Header { get; set; }
        public Transaction[] Transactions { get; set; }
        public TransactionReceipt[] Receipts { get; set; }
        public BlockHeader[] Ommers { get; }
        public Keccak Hash => Header.Hash;
        public BigInteger Number => Header.Number;
        public BigInteger Difficulty => Header.Difficulty;
        public BigInteger? TotalDifficulty => Header.TotalDifficulty;
        
        public string ToString(string indent)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"BLOCK {Number}");
            builder.AppendLine($"HEADER:");
            builder.Append($"{Header.ToString("  ")}");
            builder.AppendLine($"OMMERS:");
            foreach (BlockHeader ommer in Ommers)
            {
                builder.Append($"{ommer.ToString("  ")}");    
            }
            
            builder.AppendLine($"TRANSACTIONS:");
            foreach (Transaction tx in Transactions)
            {
                builder.Append($"{tx.ToString("  ")}");    
            }
            
            return builder.ToString();
        }

        public override string ToString()
        {
            return ToString(string.Empty);
        }
    }
}