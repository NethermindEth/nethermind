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
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Hash} ({Number})")]
    public class Block
    {
        public enum Format
        {
            Full,
            HashAndNumber,
            Short
        }

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

        public bool IsGenesis => Number == 0;

        public BlockHeader Header { get; set; }
        public Transaction[] Transactions { get; set; }
        public BlockHeader[] Ommers { get; set; }

        public Keccak Hash
        {
            get => Header.Hash;
            set => Header.Hash = value;
        }
        
        public Keccak ParentHash
        {
            get => Header.ParentHash;
            set => Header.ParentHash = value;
        }
        
        public Address Beneficiary
        {
            get => Header.Beneficiary;
            set => Header.Beneficiary = value;
        }
        
        public Keccak StateRoot
        {
            get => Header.StateRoot;
            set => Header.StateRoot = value;
        }
        
        public Keccak TransactionsRoot
        {
            get => Header.TransactionsRoot;
            set => Header.TransactionsRoot = value;
        }

        public long GasLimit
        {
            get => Header.GasLimit;
            set => Header.GasLimit = value;
        }

        public long GasUsed
        {
            get => Header.GasUsed;
            set => Header.GasUsed = value;
        }

        public UInt256 Timestamp
        {
            get => Header.Timestamp;
            set => Header.Timestamp = value;
        }

        public UInt256 Number
        {
            get => Header.Number;
            set => Header.Number = value;
        }

        public UInt256 Difficulty
        {
            get => Header.Difficulty;
            set => Header.Difficulty = value;
        }

        public UInt256? TotalDifficulty
        {
            get => Header?.TotalDifficulty;
            set => Header.TotalDifficulty = value;
        }

        public UInt256? TotalTransactions
        {
            get => Header?.TotalTransactions;
            set => Header.TotalTransactions = value;
        }

        public string ToString(string indent)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"Block {Number}");
            builder.AppendLine("  Header:");
            builder.Append($"{Header.ToString("    ")}");
            
            builder.AppendLine("  Ommers:");
            foreach (BlockHeader ommer in Ommers)
            {
                builder.Append($"{ommer.ToString("    ")}");
            }

            builder.AppendLine("  Transactions:");
            foreach (Transaction tx in Transactions)
            {
                builder.Append($"{tx.ToString("    ")}");
            }

            return builder.ToString();
        }

        public bool HasAddressesRecovered => Transactions.Length == 0 || Transactions[0].SenderAddress != null;
        
        public override string ToString()
        {
            return ToString(Format.Short);
        }

        public string ToString(Format format)
        {
            switch (format)
            {
                case Format.Full:
                    return ToString(string.Empty);
                case Format.HashAndNumber:
                    if (Hash == null)
                    {
                        return $"{Number} null";
                    }
                    else
                    {
                        return $"{Number} ({Hash})";
                    }
                default:
                    if (Hash == null)
                    {
                        return $"{Number} null";
                    }
                    else
                    {
                        return $"{Number} ({Hash.ToString().Substring(60, 6)})";
                    }
            }
        }
    }
}