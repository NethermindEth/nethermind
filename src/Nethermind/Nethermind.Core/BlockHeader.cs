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

using System.Diagnostics;
using System.Numerics;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Hash} ({Number})")]
    public class BlockHeader
    {
        public const ulong GenesisBlockNumber = 0;

        internal BlockHeader()
        {
        }

        public BlockHeader(Keccak parentHash, Keccak ommersHash, Address beneficiary, BigInteger difficulty, BigInteger number, long gasLimit, BigInteger timestamp, byte[] extraData)
        {
            ParentHash = parentHash;
            OmmersHash = ommersHash;
            Beneficiary = beneficiary;
            Difficulty = difficulty;
            Number = number;
            GasLimit = gasLimit;
            Timestamp = timestamp;
            ExtraData = extraData;
        }

        public Keccak ParentHash { get; internal set; }
        public Keccak OmmersHash { get; set; }
        public Address Beneficiary { get; set; }

        public Keccak StateRoot { get; set; }
        public Keccak TransactionsRoot { get; set; }
        public Keccak ReceiptsRoot { get; set; }
        public Bloom Bloom { get; set; }
        public BigInteger Difficulty { get; internal set; }
        public BigInteger Number { get; internal set; }
        public long GasUsed { get; set; }
        public long GasLimit { get; internal set; }
        public BigInteger Timestamp { get; internal set; }
        public byte[] ExtraData { get; set; }
        public Keccak MixHash { get; set; }
        public ulong Nonce { get; set; }
        public Keccak Hash { get; set; }
        public BigInteger? TotalDifficulty { get; set; }
        public BigInteger? TotalTransactions { get; set; }

        public static Keccak CalculateHash(Rlp headerRlp)
        {
            return Keccak.Compute(headerRlp);
        }

        public static Keccak CalculateHash(BlockHeader header)
        {
            return Keccak.Compute(Rlp.Encode(header));
        }
        
        public static Keccak CalculateHash(Block block)
        {
            return Keccak.Compute(Rlp.Encode(block.Header));
        }

        public string ToString(string indent)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"{indent}Parent: {ParentHash}");
            builder.AppendLine($"{indent}Ommers Hash: {OmmersHash}");
            builder.AppendLine($"{indent}Beneficiary: {Beneficiary}");
            builder.AppendLine($"{indent}Difficulty: {Difficulty}");
            builder.AppendLine($"{indent}Number: {Number}");
            builder.AppendLine($"{indent}Gas Limit: {GasLimit}");
            builder.AppendLine($"{indent}Gas Used: {GasUsed}");
            builder.AppendLine($"{indent}Timestamp: {Timestamp}");
            builder.AppendLine($"{indent}Extra Data: {new Hex(ExtraData ?? new byte[0])}");
            builder.AppendLine($"{indent}Mix Hash: {MixHash}");
            builder.AppendLine($"{indent}Nonce: {Nonce}");
            builder.AppendLine($"{indent}Hash: {Hash}");
            return builder.ToString();
        }

        public override string ToString()
        {
            return ToString(string.Empty);
        }

        public string ToString(Format format)
        {
            switch (format)
            {
                case Format.Full:
                    return ToString(string.Empty);
                default:
                    if (Hash == null)
                    {
                        return $"{Number} null";
                    }
                    else
                    {
                        return $"{Number} ({((string)new Hex(Hash.Bytes)).Substring(58, 6)})";
                    }
            }
        }

        public enum Format // TODO: use formatting strings / standard approach?
        {
            Full,
            Short
        }
    }
}