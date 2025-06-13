// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Test
{
    /// <summary>
    /// Only for testing purposes.
    /// If we want to override only a few properties for tests based on different releases spec we can use this class.
    /// </summary>
    public class OverridableReleaseSpec(IReleaseSpec spec) : IReleaseSpec
    {
        public string Name => "OverridableReleaseSpec";

        public long MaximumExtraDataSize => spec.MaximumExtraDataSize;

        public long MaxCodeSize => spec.MaxCodeSize;

        public long MinGasLimit => spec.MinGasLimit;

        public long GasLimitBoundDivisor => spec.GasLimitBoundDivisor;

        private UInt256? _blockReward = spec.BlockReward;
        public UInt256 BlockReward
        {
            get => _blockReward ?? spec.BlockReward;
            set => _blockReward = value;
        }

        public long DifficultyBombDelay => spec.DifficultyBombDelay;

        public long DifficultyBoundDivisor => spec.DifficultyBoundDivisor;

        public long? FixedDifficulty => spec.FixedDifficulty;

        public int MaximumUncleCount => spec.MaximumUncleCount;

        public bool IsTimeAdjustmentPostOlympic => spec.IsTimeAdjustmentPostOlympic;

        public bool IsEip2Enabled => spec.IsEip2Enabled;

        public bool IsEip7Enabled => spec.IsEip7Enabled;

        public bool IsEip100Enabled => spec.IsEip100Enabled;

        public bool IsEip140Enabled => spec.IsEip140Enabled;

        public bool IsEip150Enabled => spec.IsEip150Enabled;

        public bool IsEip155Enabled => spec.IsEip155Enabled;

        public bool IsEip158Enabled => spec.IsEip158Enabled;

        public bool IsEip160Enabled => spec.IsEip160Enabled;

        public bool IsEip170Enabled => spec.IsEip170Enabled;

        public bool IsEip196Enabled => spec.IsEip196Enabled;

        public bool IsEip197Enabled => spec.IsEip197Enabled;

        public bool IsEip198Enabled => spec.IsEip198Enabled;

        public bool IsEip211Enabled => spec.IsEip211Enabled;

        public bool IsEip214Enabled => spec.IsEip214Enabled;

        public bool IsEip649Enabled => spec.IsEip649Enabled;

        public bool IsEip658Enabled => spec.IsEip658Enabled;

        public bool IsEip145Enabled => spec.IsEip145Enabled;

        public bool IsEip1014Enabled => spec.IsEip1014Enabled;

        public bool IsEip1052Enabled => spec.IsEip1052Enabled;

        public bool IsEip1283Enabled => spec.IsEip1283Enabled;

        public bool IsEip1234Enabled => spec.IsEip1234Enabled;

        public bool IsEip1344Enabled => spec.IsEip1344Enabled;

        public bool IsEip2028Enabled => spec.IsEip2028Enabled;

        public bool IsEip152Enabled => spec.IsEip152Enabled;

        public bool IsEip1108Enabled => spec.IsEip1108Enabled;

        public bool IsEip1884Enabled => spec.IsEip1884Enabled;

        public bool IsEip2200Enabled => spec.IsEip2200Enabled;

        public bool IsEip2537Enabled => spec.IsEip2537Enabled;

        public bool IsEip2565Enabled => spec.IsEip2565Enabled;

        public bool IsEip2929Enabled => spec.IsEip2929Enabled;

        public bool IsEip2930Enabled => spec.IsEip2930Enabled;

        public bool IsEip1559Enabled => spec.IsEip1559Enabled;
        public bool IsEip3198Enabled => spec.IsEip3198Enabled;
        public bool IsEip3529Enabled => spec.IsEip3529Enabled;

        public bool IsEip3541Enabled => spec.IsEip3541Enabled;
        public bool IsEip4844Enabled => spec.IsEip4844Enabled;
        public bool IsRip7212Enabled => spec.IsRip7212Enabled;
        public bool IsOpGraniteEnabled => spec.IsOpGraniteEnabled;
        public bool IsOpHoloceneEnabled => spec.IsOpHoloceneEnabled;
        public bool IsOpIsthmusEnabled => spec.IsOpIsthmusEnabled;

        public bool IsEip7623Enabled => spec.IsEip7623Enabled;
        public bool IsEip7918Enabled => spec.IsEip7918Enabled;

        public bool IsEip7883Enabled => spec.IsEip7883Enabled;

        public bool IsEip3607Enabled { get; set; } = spec.IsEip3607Enabled;

        public bool IsEip158IgnoredAccount(Address address) => spec.IsEip158IgnoredAccount(address);

        private long? _overridenEip1559TransitionBlock;
        public long Eip1559TransitionBlock
        {
            get => _overridenEip1559TransitionBlock ?? spec.Eip1559TransitionBlock;
            set => _overridenEip1559TransitionBlock = value;
        }

        private Address? _overridenFeeCollector;
        public Address? FeeCollector
        {
            get => _overridenFeeCollector ?? spec.FeeCollector;
            set => _overridenFeeCollector = value;
        }

        private ulong? _overridenEip4844TransitionTimeStamp;

        public ulong Eip4844TransitionTimestamp
        {
            get
            {
                return _overridenEip4844TransitionTimeStamp ?? spec.Eip4844TransitionTimestamp;
            }
            set
            {
                _overridenEip4844TransitionTimeStamp = value;
            }
        }

        public ulong TargetBlobCount => spec.TargetBlobCount;
        public ulong MaxBlobCount => spec.MaxBlobCount;
        public ulong MaxBlobsPerTx => spec.MaxBlobsPerTx;
        public UInt256 BlobBaseFeeUpdateFraction => spec.BlobBaseFeeUpdateFraction;
        public bool IsEip1153Enabled => spec.IsEip1153Enabled;
        public bool IsEip3651Enabled => spec.IsEip3651Enabled;
        public bool IsEip3855Enabled => spec.IsEip3855Enabled;
        public bool IsEip3860Enabled => spec.IsEip3860Enabled;
        public bool IsEip4895Enabled => spec.IsEip4895Enabled;
        public ulong WithdrawalTimestamp => spec.WithdrawalTimestamp;
        public bool IsEip5656Enabled => spec.IsEip5656Enabled;
        public bool IsEip6780Enabled => spec.IsEip6780Enabled;
        public bool IsEip4788Enabled => spec.IsEip4788Enabled;
        public bool IsEip4844FeeCollectorEnabled => spec.IsEip4844FeeCollectorEnabled;
        public Address? Eip4788ContractAddress => spec.Eip4788ContractAddress;
        public bool IsEip7002Enabled => spec.IsEip7002Enabled;
        public Address Eip7002ContractAddress => spec.Eip7002ContractAddress;
        public bool IsEip7251Enabled => spec.IsEip7251Enabled;
        public Address Eip7251ContractAddress => spec.Eip7251ContractAddress;
        public bool IsEip2935Enabled => spec.IsEip2935Enabled;
        public bool IsEip7709Enabled => spec.IsEip7709Enabled;
        public Address Eip2935ContractAddress => spec.Eip2935ContractAddress;
        public bool IsEip7702Enabled => spec.IsEip7702Enabled;
        public bool IsEip7823Enabled => spec.IsEip7823Enabled;
        public bool IsEip7825Enabled { get; set; } = spec.IsEip7825Enabled;
        public UInt256 ForkBaseFee => spec.ForkBaseFee;
        public UInt256 BaseFeeMaxChangeDenominator => spec.BaseFeeMaxChangeDenominator;
        public long ElasticityMultiplier => spec.ElasticityMultiplier;
        public IBaseFeeCalculator BaseFeeCalculator => spec.BaseFeeCalculator;
        public bool IsEofEnabled => spec.IsEofEnabled;
        public bool IsEip6110Enabled => spec.IsEip6110Enabled;
        public Address DepositContractAddress => spec.DepositContractAddress;
        public bool IsEip7594Enabled => spec.IsEip7594Enabled;

        Array? IReleaseSpec.EvmInstructionsNoTrace { get => spec.EvmInstructionsNoTrace; set => spec.EvmInstructionsNoTrace = value; }
        Array? IReleaseSpec.EvmInstructionsTraced { get => spec.EvmInstructionsTraced; set => spec.EvmInstructionsTraced = value; }
        public bool IsEip7939Enabled => spec.IsEip7939Enabled;
    }
}
