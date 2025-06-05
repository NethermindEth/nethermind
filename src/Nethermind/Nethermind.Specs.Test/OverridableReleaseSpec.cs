// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
    public class OverridableReleaseSpec : IReleaseSpec
    {
        private readonly IReleaseSpec _spec;

        public OverridableReleaseSpec(IReleaseSpec spec)
        {
            _spec = spec;
            IsEip3607Enabled = _spec.IsEip3607Enabled;
            IsEip7825Enabled = _spec.IsEip7825Enabled;
            BlockReward = _spec.BlockReward;
        }

        public string Name => "OverridableReleaseSpec";

        public long MaximumExtraDataSize => _spec.MaximumExtraDataSize;

        public long MaxCodeSize => _spec.MaxCodeSize;

        public long MinGasLimit => _spec.MinGasLimit;

        public long GasLimitBoundDivisor => _spec.GasLimitBoundDivisor;

        private UInt256? _blockReward;
        public UInt256 BlockReward
        {
            get => _blockReward ?? _spec.BlockReward;
            set => _blockReward = value;
        }

        public long DifficultyBombDelay => _spec.DifficultyBombDelay;

        public long DifficultyBoundDivisor => _spec.DifficultyBoundDivisor;

        public long? FixedDifficulty => _spec.FixedDifficulty;

        public int MaximumUncleCount => _spec.MaximumUncleCount;

        public bool IsTimeAdjustmentPostOlympic => _spec.IsTimeAdjustmentPostOlympic;

        public bool IsEip2Enabled => _spec.IsEip2Enabled;

        public bool IsEip7Enabled => _spec.IsEip7Enabled;

        public bool IsEip100Enabled => _spec.IsEip100Enabled;

        public bool IsEip140Enabled => _spec.IsEip140Enabled;

        public bool IsEip150Enabled => _spec.IsEip150Enabled;

        public bool IsEip155Enabled => _spec.IsEip155Enabled;

        public bool IsEip158Enabled => _spec.IsEip158Enabled;

        public bool IsEip160Enabled => _spec.IsEip160Enabled;

        public bool IsEip170Enabled => _spec.IsEip170Enabled;

        public bool IsEip196Enabled => _spec.IsEip196Enabled;

        public bool IsEip197Enabled => _spec.IsEip197Enabled;

        public bool IsEip198Enabled => _spec.IsEip198Enabled;

        public bool IsEip211Enabled => _spec.IsEip211Enabled;

        public bool IsEip214Enabled => _spec.IsEip214Enabled;

        public bool IsEip649Enabled => _spec.IsEip649Enabled;

        public bool IsEip658Enabled => _spec.IsEip658Enabled;

        public bool IsEip145Enabled => _spec.IsEip145Enabled;

        public bool IsEip1014Enabled => _spec.IsEip1014Enabled;

        public bool IsEip1052Enabled => _spec.IsEip1052Enabled;

        public bool IsEip1283Enabled => _spec.IsEip1283Enabled;

        public bool IsEip1234Enabled => _spec.IsEip1234Enabled;

        public bool IsEip1344Enabled => _spec.IsEip1344Enabled;

        public bool IsEip2028Enabled => _spec.IsEip2028Enabled;

        public bool IsEip152Enabled => _spec.IsEip152Enabled;

        public bool IsEip1108Enabled => _spec.IsEip1108Enabled;

        public bool IsEip1884Enabled => _spec.IsEip1884Enabled;

        public bool IsEip2200Enabled => _spec.IsEip2200Enabled;

        public bool IsEip2537Enabled => _spec.IsEip2537Enabled;

        public bool IsEip2565Enabled => _spec.IsEip2565Enabled;

        public bool IsEip2929Enabled => _spec.IsEip2929Enabled;

        public bool IsEip2930Enabled => _spec.IsEip2930Enabled;

        public bool IsEip1559Enabled => _spec.IsEip1559Enabled;
        public bool IsEip3198Enabled => _spec.IsEip3198Enabled;
        public bool IsEip3529Enabled => _spec.IsEip3529Enabled;

        public bool IsEip3541Enabled => _spec.IsEip3541Enabled;
        public bool IsEip4844Enabled => _spec.IsEip4844Enabled;
        public bool IsRip7212Enabled => _spec.IsRip7212Enabled;
        public bool IsOpGraniteEnabled => _spec.IsOpGraniteEnabled;
        public bool IsOpHoloceneEnabled => _spec.IsOpHoloceneEnabled;
        public bool IsOpIsthmusEnabled => _spec.IsOpIsthmusEnabled;

        public bool IsEip7623Enabled => _spec.IsEip7623Enabled;
        public bool IsEip7918Enabled => _spec.IsEip7918Enabled;

        public bool IsEip7883Enabled => _spec.IsEip7883Enabled;

        public bool IsEip3607Enabled { get; set; }

        public bool IsEip158IgnoredAccount(Address address) => _spec.IsEip158IgnoredAccount(address);

        private long? _overridenEip1559TransitionBlock;
        public long Eip1559TransitionBlock
        {
            get => _overridenEip1559TransitionBlock ?? _spec.Eip1559TransitionBlock;
            set => _overridenEip1559TransitionBlock = value;
        }

        private Address? _overridenFeeCollector;
        public Address? FeeCollector
        {
            get => _overridenFeeCollector ?? _spec.FeeCollector;
            set => _overridenFeeCollector = value;
        }

        private ulong? _overridenEip4844TransitionTimeStamp;

        public ulong Eip4844TransitionTimestamp
        {
            get
            {
                return _overridenEip4844TransitionTimeStamp ?? _spec.Eip4844TransitionTimestamp;
            }
            set
            {
                _overridenEip4844TransitionTimeStamp = value;
            }
        }

        public ulong TargetBlobCount => _spec.TargetBlobCount;
        public ulong MaxBlobCount => _spec.MaxBlobCount;
        public UInt256 BlobBaseFeeUpdateFraction => _spec.BlobBaseFeeUpdateFraction;
        public bool IsEip1153Enabled => _spec.IsEip1153Enabled;
        public bool IsEip3651Enabled => _spec.IsEip3651Enabled;
        public bool IsEip3855Enabled => _spec.IsEip3855Enabled;
        public bool IsEip3860Enabled => _spec.IsEip3860Enabled;
        public bool IsEip4895Enabled => _spec.IsEip4895Enabled;
        public ulong WithdrawalTimestamp => _spec.WithdrawalTimestamp;
        public bool IsEip5656Enabled => _spec.IsEip5656Enabled;
        public bool IsEip6780Enabled => _spec.IsEip6780Enabled;
        public bool IsEip4788Enabled => _spec.IsEip4788Enabled;
        public bool IsEip4844FeeCollectorEnabled => _spec.IsEip4844FeeCollectorEnabled;
        public Address? Eip4788ContractAddress => _spec.Eip4788ContractAddress;
        public bool IsEip7002Enabled => _spec.IsEip7002Enabled;
        public Address Eip7002ContractAddress => _spec.Eip7002ContractAddress;
        public bool IsEip7251Enabled => _spec.IsEip7251Enabled;
        public Address Eip7251ContractAddress => _spec.Eip7251ContractAddress;
        public bool IsEip2935Enabled => _spec.IsEip2935Enabled;
        public bool IsEip7709Enabled => _spec.IsEip7709Enabled;
        public Address Eip2935ContractAddress => _spec.Eip2935ContractAddress;
        public bool IsEip7702Enabled => _spec.IsEip7702Enabled;
        public bool IsEip7823Enabled => _spec.IsEip7823Enabled;
        public bool IsEip7825Enabled { get; set; }
        public UInt256 ForkBaseFee => _spec.ForkBaseFee;
        public UInt256 BaseFeeMaxChangeDenominator => _spec.BaseFeeMaxChangeDenominator;
        public long ElasticityMultiplier => _spec.ElasticityMultiplier;
        public IBaseFeeCalculator BaseFeeCalculator => _spec.BaseFeeCalculator;
        public bool IsEofEnabled => _spec.IsEofEnabled;
        public bool IsEip6110Enabled => _spec.IsEip6110Enabled;
        public Address DepositContractAddress => _spec.DepositContractAddress;
        public bool IsEip7594Enabled => _spec.IsEip7594Enabled;

        Array? IReleaseSpec.EvmInstructionsNoTrace { get => _spec.EvmInstructionsNoTrace; set => _spec.EvmInstructionsNoTrace = value; }
        Array? IReleaseSpec.EvmInstructionsTraced { get => _spec.EvmInstructionsTraced; set => _spec.EvmInstructionsTraced = value; }
    }
}
