//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Nethermind.Core.Crypto;
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

        public Block(BlockHeader blockHeader)
            : this(blockHeader, BlockBody.Empty)
        {
        }

        public bool IsGenesis => Header.IsGenesis;

        public Transaction[] Transactions => Body?.Transactions;
        
        public BlockHeader[] Ommers => Body?.Ommers;
        
        public BlockHeader Header { get; set; }
        public BlockBody Body { get; set; }

        public Keccak Hash => Header.Hash;

        public Keccak ParentHash => Header.ParentHash;

        public ulong Nonce => Header.Nonce;

        public Keccak MixHash => Header.MixHash;

        public byte[] ExtraData => Header.ExtraData;

        public Bloom Bloom => Header.Bloom;

        public Keccak OmmersHash => Header.OmmersHash;

        public Address Beneficiary => Header.Beneficiary;

        public Address Author => Header.Author;

        public Keccak StateRoot => Header.StateRoot;

        public Keccak TxRoot => Header.TxRoot;

        public Keccak ReceiptsRoot => Header.ReceiptsRoot;

        public long GasLimit => Header.GasLimit;

        public long GasUsed => Header.GasUsed;

        public UInt256 Timestamp => Header.Timestamp;

        public DateTime TimestampDate => Header.TimestampDate;

        public long Number => Header.Number;

        public UInt256 Difficulty => Header.Difficulty;

        public UInt256? TotalDifficulty => Header?.TotalDifficulty;

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
                    return Hash == null ? $"{Number} null" : $"{Number} ({Hash})";
                case Format.HashNumberAndTx:
                    return Hash == null ? $"{Number} null, tx count: {Body.Transactions.Length}" : $"{Number} {TimestampDate:HH:mm:ss} ({Hash?.ToShortString()}), tx count: {Body.Transactions.Length}";
                case Format.HashNumberDiffAndTx:
                    return Hash == null ? $"{Number} null, diff: {Difficulty}, tx count: {Body.Transactions.Length}" : $"{Number} ({Hash?.ToShortString()}), diff: {Difficulty}, tx count: {Body.Transactions.Length}";
                default:
                    return Hash == null ? $"{Number} null" : $"{Number} ({Hash?.ToShortString()})";
            }
        }
    }
}