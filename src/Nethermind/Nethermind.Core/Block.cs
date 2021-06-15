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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Hash} ({Number})")]
    public class Block
    {
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

        public Block WithReplacedHeader(BlockHeader newHeader)
        {
            return new(newHeader, Body);
        }

        public Block WithReplacedBody(BlockBody newBody)
        {
            return new(Header, newBody);
        }
        
        public BlockHeader Header { get; }

        public BlockBody Body { get; }

        public bool IsGenesis => Header.IsGenesis;

        public Transaction[] Transactions { get => Body.Transactions; protected set => Body.Transactions = value; } // setter needed to produce blocks with unknown transaction count on start

        public BlockHeader[] Ommers => Body.Ommers; // do not add setter here

        public Keccak? Hash => Header.Hash; // do not add setter here

        public Keccak? ParentHash => Header.ParentHash; // do not add setter here

        public ulong Nonce => Header.Nonce; // do not add setter here

        public Keccak? MixHash => Header.MixHash; // do not add setter here

        public byte[]? ExtraData => Header.ExtraData; // do not add setter here

        public Bloom? Bloom => Header.Bloom; // do not add setter here

        public Keccak? OmmersHash => Header.OmmersHash; // do not add setter here

        public Address? Beneficiary => Header.Beneficiary; // do not add setter here

        public Address? Author => Header.Author; // do not add setter here

        public Keccak? StateRoot => Header.StateRoot; // do not add setter here

        public Keccak? TxRoot => Header.TxRoot; // do not add setter here

        public Keccak? ReceiptsRoot => Header.ReceiptsRoot; // do not add setter here

        public long GasLimit => Header.GasLimit; // do not add setter here

        public long GasUsed => Header.GasUsed; // do not add setter here

        public UInt256 Timestamp => Header.Timestamp; // do not add setter here

        public DateTime TimestampDate => Header.TimestampDate; // do not add setter here

        public long Number => Header.Number; // do not add setter here

        public UInt256 Difficulty => Header.Difficulty; // do not add setter here

        public UInt256? TotalDifficulty => Header.TotalDifficulty; // do not add setter here
        
        public UInt256 BaseFeePerGas => Header.BaseFeePerGas; // do not add setter here

        public override string ToString()
        {
            return ToString(Format.Short);
        }

        public string ToString(Format format)
        {
            return format switch
            {
                Format.Full => ToFullString(),
                Format.FullHashAndNumber => Hash == null ? $"{Number} null" : $"{Number} ({Hash})",
                Format.HashNumberAndTx => Hash == null
                    ? $"{Number} null, tx count: {Body.Transactions.Length}"
                    : $"{Number} {TimestampDate:HH:mm:ss} ({Hash?.ToShortString()}), tx count: {Body.Transactions.Length}",
                Format.HashNumberDiffAndTx => Hash == null
                    ? $"{Number} null, diff: {Difficulty}, tx count: {Body.Transactions.Length}"
                    : $"{Number} ({Hash?.ToShortString()}), diff: {Difficulty}, tx count: {Body.Transactions.Length}",
                _ => Hash == null ? $"{Number} null" : $"{Number} ({Hash?.ToShortString()})"
            };
        }

        private string ToFullString()
        {
            StringBuilder builder = new();
            builder.AppendLine($"Block {Number}");
            builder.AppendLine("  Header:");
            builder.Append($"{Header.ToString("    ")}");

            builder.AppendLine("  Ommers:");
            foreach (BlockHeader ommer in Body.Ommers ?? Array.Empty<BlockHeader>())
            {
                builder.Append($"{ommer.ToString("    ")}");
            }

            builder.AppendLine("  Transactions:");
            foreach (Transaction tx in Body?.Transactions ?? Array.Empty<Transaction>())
            {
                builder.Append($"{tx.ToString("    ")}");
            }

            return builder.ToString();
        }

        public enum Format
        {
            Full,
            FullHashAndNumber,
            HashNumberAndTx,
            HashNumberDiffAndTx,
            Short
        }
    }
}
