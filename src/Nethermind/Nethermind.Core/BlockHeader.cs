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
using System.Diagnostics;
using System.Text;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Hash} ({Number})")]
    public class BlockHeader
    {
        internal BlockHeader() { }

        public BlockHeader(
            Keccak parentHash,
            Keccak ommersHash,
            Address beneficiary,
            UInt256 difficulty,
            long number,
            long gasLimit,
            UInt256 timestamp,
            byte[] extraData)
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

        public WeakReference<BlockHeader>? MaybeParent { get; set; }

        public bool IsGenesis => Number == 0L;
        public Keccak? ParentHash { get; set; }
        public Keccak? OmmersHash { get; set; }
        public Address? Author { get; set; }
        public Address? Beneficiary { get; set; }
        public Address? GasBeneficiary => Author ?? Beneficiary;
        public Keccak? StateRoot { get; set; }
        public Keccak? TxRoot { get; set; }
        public Keccak? ReceiptsRoot { get; set; }
        public Bloom? Bloom { get; set; }
        public UInt256 Difficulty { get; set; }
        public long Number { get; set; }
        public long GasUsed { get; set; }
        public long GasLimit { get; set; }
        public UInt256 Timestamp { get; set; }
        public DateTime TimestampDate => DateTimeOffset.FromUnixTimeSeconds((long)Timestamp).LocalDateTime;
        public byte[] ExtraData { get; set; } = Array.Empty<byte>();
        public Keccak? MixHash { get; set; }
        public ulong Nonce { get; set; }
        public Keccak? Hash { get; set; }
        public UInt256? TotalDifficulty { get; set; }
        public byte[]? AuRaSignature { get; set; }
        public long? AuRaStep { get; set; }
        public UInt256 BaseFeePerGas { get; set; }

        public bool HasBody => OmmersHash != Keccak.OfAnEmptySequenceRlp || TxRoot != Keccak.EmptyTreeHash;
        public string SealEngineType { get; set; } = Nethermind.Core.SealEngineType.Ethash;

        public string ToString(string indent)
        {
            StringBuilder builder = new();
            builder.AppendLine($"{indent}Hash: {Hash}");
            builder.AppendLine($"{indent}Number: {Number}");
            builder.AppendLine($"{indent}Parent: {ParentHash}");
            builder.AppendLine($"{indent}Beneficiary: {Beneficiary}");
            builder.AppendLine($"{indent}Gas Limit: {GasLimit}");
            builder.AppendLine($"{indent}Gas Used: {GasUsed}");
            builder.AppendLine($"{indent}Timestamp: {Timestamp}");
            builder.AppendLine($"{indent}Extra Data: {ExtraData.ToHexString()}");
            builder.AppendLine($"{indent}Difficulty: {Difficulty}");
            builder.AppendLine($"{indent}Mix Hash: {MixHash}");
            builder.AppendLine($"{indent}Nonce: {Nonce}");
            builder.AppendLine($"{indent}Ommers Hash: {OmmersHash}");
            builder.AppendLine($"{indent}Tx Root: {TxRoot}");
            builder.AppendLine($"{indent}Receipts Root: {ReceiptsRoot}");
            builder.AppendLine($"{indent}State Root: {StateRoot}");
            builder.AppendLine($"{indent}BaseFeePerGas: {BaseFeePerGas}");

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
                case Format.FullHashAndNumber:
                    return Hash == null ? $"{Number} null" : $"{Number} ({Hash})";
                default:
                    return Hash == null ? $"{Number} null" : $"{Number} ({Hash.ToShortString()})";
            }
        }

        [Todo(Improve.Refactor, "Use IFormattable here")]
        public enum Format
        {
            Full,
            Short,
            FullHashAndNumber
        }
    }
}
