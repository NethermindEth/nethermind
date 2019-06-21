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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Hash} ({Number})")]
    public class Block
    {
        public enum Format
        {
            Full,
            FullHashAndNumber,
            HashNumberAndTx,
            HashNumberDiffAndTx,
            Short
        }

        private Block()
        {
            Body = new BlockBody();
        }

        public Block(BlockHeader blockHeader, BlockBody body)
        {
            Header = blockHeader;
            Body = body;
        }
        
        public Block(BlockHeader blockHeader, IEnumerable<Transaction> transactions, IEnumerable<BlockHeader> ommers)
        {
            Header = blockHeader;
            Body = new BlockBody(transactions.ToArray(), ommers.ToArray());
        }

        public Block(BlockHeader blockHeader, params BlockHeader[] ommers)
            : this(blockHeader, Enumerable.Empty<Transaction>(), ommers)
        {
        }

        public bool IsGenesis => Header.IsGenesis;

        public Transaction[] Transactions => Body?.Transactions;
        
        public BlockHeader[] Ommers => Body?.Ommers;
        
        public BlockHeader Header { get; set; }
        public BlockBody Body { get; set; }

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

        public ulong Nonce
        {
            get => Header.Nonce;
            set => Header.Nonce = value;
        }

        public Keccak MixHash
        {
            get => Header.MixHash;
            set => Header.MixHash = value;
        }

        public byte[] ExtraData
        {
            get => Header.ExtraData;
            set => Header.ExtraData = value;
        }

        public Bloom Bloom
        {
            get => Header.Bloom;
            set => Header.Bloom = value;
        }

        public Keccak OmmersHash
        {
            get => Header.OmmersHash;
            set => Header.OmmersHash = value;
        }

        public Address Beneficiary
        {
            get => Header.Beneficiary;
            set => Header.Beneficiary = value;
        }

        public Address Author
        {
            get => Header.Author;
            set => Header.Author = value;
        }

        public Keccak StateRoot
        {
            get => Header.StateRoot;
            set => Header.StateRoot = value;
        }

        public Keccak TransactionsRoot
        {
            get => Header.TxRoot;
            set => Header.TxRoot = value;
        }

        public Keccak ReceiptsRoot
        {
            get => Header.ReceiptsRoot;
            set => Header.ReceiptsRoot = value;
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

        public DateTime TimestampDate => Header.TimestampDate;

        public long Number
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

        public string ToString(string indent)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"Block {Number}");
            builder.AppendLine("  Header:");
            builder.Append($"{Header.ToString("    ")}");

            builder.AppendLine("  Ommers:");
            foreach (BlockHeader ommer in Body.Ommers)
            {
                builder.Append($"{ommer.ToString("    ")}");
            }

            builder.AppendLine("  Transactions:");
            foreach (Transaction tx in Body.Transactions)
            {
                builder.Append($"{tx.ToString("    ")}");
            }

            return builder.ToString();
        }
        
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
                case Format.FullHashAndNumber:
                    if (Hash == null)
                    {
                        return $"{Number} null";
                    }
                    else
                    {
                        return $"{Number} ({Hash})";
                    }
                case Format.HashNumberAndTx:
                    if (Hash == null)
                    {
                        return $"{Number} null, tx count: {Body.Transactions.Length}";
                    }
                    else
                    {
                        return $"{Number} {TimestampDate:HH:mm:ss} ({Hash?.ToShortString()}), tx count: {Body.Transactions.Length}";
                    }
                case Format.HashNumberDiffAndTx:
                    if (Hash == null)
                    {
                        return $"{Number} null, diff: {Difficulty}, tx count: {Body.Transactions.Length}";
                    }
                    else
                    {
                        return $"{Number} ({Hash?.ToShortString()}), diff: {Difficulty}, tx count: {Body.Transactions.Length}";
                    }
                default:
                    if (Hash == null)
                    {
                        return $"{Number} null";
                    }

                    return $"{Number} ({Hash?.ToShortString()})";
            }
        }
    }
}