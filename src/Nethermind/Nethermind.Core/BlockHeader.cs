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
        public long GasUsedLegacy { get; set; }
        public long GasUsedEip1559 { get; set; }
        public long GasUsed => GasUsedEip1559 + GasUsedLegacy;
        public long GasLimit { get; set; }

        public long GasTarget // just rename the field but the meaning changes
        {
            get => GasLimit;
            set => GasLimit = value;
        }

        public long GetGasTarget1559(IReleaseSpec releaseSpec)
        {
            long transitionBlock = releaseSpec.Eip1559TransitionBlock;
            long migrationDuration = releaseSpec.Eip1559MigrationDuration;
            long finalBlock = transitionBlock + migrationDuration;
            if (Number < transitionBlock)
            {
                return 0L;
            }

            if (Number == transitionBlock)
            {
                return GasLimit / 2;
            }

            if (Number >= finalBlock)
            {
                return GasTarget;
            }

            return (GasTarget + GasTarget * (Number - transitionBlock) / migrationDuration) / 2;
        }

        public long GetGasTargetLegacy(IReleaseSpec releaseSpec) => GasLimit - GetGasTarget1559(releaseSpec);
        public UInt256 Timestamp { get; set; }
        public DateTime TimestampDate => DateTimeOffset.FromUnixTimeSeconds((long) Timestamp).LocalDateTime;
        public byte[]? ExtraData { get; set; }
        public Keccak? MixHash { get; set; }
        public ulong Nonce { get; set; }
        public Keccak? Hash { get; set; }
        public UInt256? TotalDifficulty { get; set; }
        public byte[]? AuRaSignature { get; set; }
        public long? AuRaStep { get; set; }
        public UInt256 BaseFee { get; set; }

        public bool HasBody => OmmersHash != Keccak.OfAnEmptySequenceRlp || TxRoot != Keccak.EmptyTreeHash;
        public SealEngineType SealEngineType { get; set; } = SealEngineType.Ethash;

        public string ToString(string indent)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"{indent}Hash: {Hash}");
            builder.AppendLine($"{indent}Number: {Number}");
            builder.AppendLine($"{indent}Parent: {ParentHash}");
            builder.AppendLine($"{indent}Beneficiary: {Beneficiary}");
            builder.AppendLine($"{indent}Gas Limit: {GasLimit}");
            builder.AppendLine($"{indent}Gas Used: {GasUsed}");
            builder.AppendLine($"{indent}Timestamp: {Timestamp}");
            builder.AppendLine($"{indent}Extra Data: {(ExtraData ?? new byte[0]).ToHexString()}");
            builder.AppendLine($"{indent}Difficulty: {Difficulty}");
            builder.AppendLine($"{indent}Mix Hash: {MixHash}");
            builder.AppendLine($"{indent}Nonce: {Nonce}");
            builder.AppendLine($"{indent}Ommers Hash: {OmmersHash}");
            builder.AppendLine($"{indent}Tx Root: {TxRoot}");
            builder.AppendLine($"{indent}Receipts Root: {ReceiptsRoot}");
            builder.AppendLine($"{indent}State Root: {StateRoot}");
            builder.AppendLine($"{indent}Base Fee: {BaseFee}");

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
        
        private static readonly UInt256 BaseFeeMaxChangeDenominator = 8;
        
        public static UInt256 CalculateBaseFee(BlockHeader parent, IReleaseSpec spec)
        {
            UInt256 expectedBaseFee = UInt256.Zero;
            if (spec.IsEip1559Enabled)
            {
                long gasDelta;
                UInt256 feeDelta;
                long gasTarget = parent.GetGasTarget1559(spec);

                // # check if the base fee is correct
                //   if parent_gas_used == parent_gas_target:
                //   expected_base_fee = parent_base_fee
                //   elif parent_gas_used > parent_gas_target:
                //   gas_delta = parent_gas_used - parent_gas_target
                //   fee_delta = max(parent_base_fee * gas_delta // parent_gas_target // BASE_FEE_MAX_CHANGE_DENOMINATOR, 1)
                //   expected_base_fee = parent_base_fee + fee_delta
                //   else:
                //   gas_delta = parent_gas_target - parent_gas_used
                //   fee_delta = parent_base_fee * gas_delta // parent_gas_target // BASE_FEE_MAX_CHANGE_DENOMINATOR
                //   expected_base_fee = parent_base_fee - fee_delta
                //   assert expected_base_fee == block.base_fee, 'invalid block: base fee not correct'

                if (parent.GasUsed == gasTarget)
                {
                    expectedBaseFee = parent.BaseFee;
                }
                else if (parent.GasUsed > gasTarget)
                {
                    gasDelta = parent.GasUsed - gasTarget;
                    feeDelta = UInt256.Max(
                        parent.BaseFee * (UInt256) gasDelta / (UInt256) gasTarget / BaseFeeMaxChangeDenominator,
                        UInt256.One);
                    expectedBaseFee = parent.BaseFee + feeDelta;
                }
                else
                {
                    gasDelta = gasTarget - parent.GasUsed;
                    feeDelta = parent.BaseFee * (UInt256) gasDelta / (UInt256) gasTarget / BaseFeeMaxChangeDenominator;
                    expectedBaseFee = parent.BaseFee - feeDelta;
                }

                if (spec.Eip1559TransitionBlock == parent.Number + 1)
                {
                    expectedBaseFee = 1.GWei();
                }
            }

            return expectedBaseFee;
        }
    }
}
