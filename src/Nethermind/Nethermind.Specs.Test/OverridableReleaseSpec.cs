// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
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
        public long MaximumExtraDataSize { get; set; } = spec.MaximumExtraDataSize;
        public long MaxCodeSize { get; set; } = spec.MaxCodeSize;
        public long MinGasLimit { get; set; } = spec.MinGasLimit;
        public long MinHistoryRetentionEpochs { get; set; } = spec.MinHistoryRetentionEpochs;
        public long GasLimitBoundDivisor { get; set; } = spec.GasLimitBoundDivisor;
        public UInt256 BlockReward { get; set; } = spec.BlockReward;
        public long DifficultyBombDelay { get; set; } = spec.DifficultyBombDelay;
        public long DifficultyBoundDivisor { get; set; } = spec.DifficultyBoundDivisor;
        public long? FixedDifficulty { get; set; } = spec.FixedDifficulty;
        public int MaximumUncleCount { get; set; } = spec.MaximumUncleCount;
        public bool IsTimeAdjustmentPostOlympic { get; set; } = spec.IsTimeAdjustmentPostOlympic;
        public bool IsEip2Enabled { get; set; } = spec.IsEip2Enabled;
        public bool IsEip7Enabled { get; set; } = spec.IsEip7Enabled;
        public bool IsEip100Enabled { get; set; } = spec.IsEip100Enabled;
        public bool IsEip140Enabled { get; set; } = spec.IsEip140Enabled;
        public bool IsEip150Enabled { get; set; } = spec.IsEip150Enabled;
        public bool IsEip155Enabled { get; set; } = spec.IsEip155Enabled;
        public bool IsEip158Enabled { get; set; } = spec.IsEip158Enabled;
        public bool IsEip160Enabled { get; set; } = spec.IsEip160Enabled;
        public bool IsEip170Enabled { get; set; } = spec.IsEip170Enabled;
        public bool IsEip196Enabled { get; set; } = spec.IsEip196Enabled;
        public bool IsEip197Enabled { get; set; } = spec.IsEip197Enabled;
        public bool IsEip198Enabled { get; set; } = spec.IsEip198Enabled;
        public bool IsEip211Enabled { get; set; } = spec.IsEip211Enabled;
        public bool IsEip214Enabled { get; set; } = spec.IsEip214Enabled;
        public bool IsEip649Enabled { get; set; } = spec.IsEip649Enabled;
        public bool IsEip658Enabled { get; set; } = spec.IsEip658Enabled;
        public bool IsEip145Enabled { get; set; } = spec.IsEip145Enabled;
        public bool IsEip1014Enabled { get; set; } = spec.IsEip1014Enabled;
        public bool IsEip1052Enabled { get; set; } = spec.IsEip1052Enabled;
        public bool IsEip1283Enabled { get; set; } = spec.IsEip1283Enabled;
        public bool IsEip1234Enabled { get; set; } = spec.IsEip1234Enabled;
        public bool IsEip1344Enabled { get; set; } = spec.IsEip1344Enabled;
        public bool IsEip2028Enabled { get; set; } = spec.IsEip2028Enabled;
        public bool IsEip152Enabled { get; set; } = spec.IsEip152Enabled;
        public bool IsEip1108Enabled { get; set; } = spec.IsEip1108Enabled;
        public bool IsEip1884Enabled { get; set; } = spec.IsEip1884Enabled;
        public bool IsEip2200Enabled { get; set; } = spec.IsEip2200Enabled;
        public bool IsEip2537Enabled { get; set; } = spec.IsEip2537Enabled;
        public bool IsEip2565Enabled { get; set; } = spec.IsEip2565Enabled;
        public bool IsEip2929Enabled { get; set; } = spec.IsEip2929Enabled;
        public bool IsEip2930Enabled { get; set; } = spec.IsEip2930Enabled;
        public bool IsEip1559Enabled { get; set; } = spec.IsEip1559Enabled;
        public bool IsEip3198Enabled { get; set; } = spec.IsEip3198Enabled;
        public bool IsEip3529Enabled { get; set; } = spec.IsEip3529Enabled;
        public bool IsEip3541Enabled { get; set; } = spec.IsEip3541Enabled;
        public bool IsEip4844Enabled { get; set; } = spec.IsEip4844Enabled;
        public bool IsEip7951Enabled { get; set; } = spec.IsEip7951Enabled;
        public bool IsRip7212Enabled { get; set; } = spec.IsRip7212Enabled;
        public bool IsOpGraniteEnabled { get; set; } = spec.IsOpGraniteEnabled;
        public bool IsOpHoloceneEnabled { get; set; } = spec.IsOpHoloceneEnabled;
        public bool IsOpIsthmusEnabled { get; set; } = spec.IsOpIsthmusEnabled;
        public bool IsOpJovianEnabled { get; set; } = spec.IsOpJovianEnabled;
        public bool IsEip7623Enabled { get; set; } = spec.IsEip7623Enabled;
        public bool IsEip7918Enabled { get; set; } = spec.IsEip7918Enabled;
        public bool IsEip7883Enabled { get; set; } = spec.IsEip7883Enabled;
        public bool IsEip7934Enabled { get; set; } = spec.IsEip7934Enabled;
        public int Eip7934MaxRlpBlockSize { get; set; } = spec.Eip7934MaxRlpBlockSize;
        public bool ValidateChainId { get; set; } = spec.ValidateChainId;
        public bool IsEip3607Enabled { get; set; } = spec.IsEip3607Enabled;
        public bool IsEip158IgnoredAccount(Address address) => spec.IsEip158IgnoredAccount(address);
        public long Eip1559TransitionBlock { get; set; } = spec.Eip1559TransitionBlock;
        public Address? FeeCollector { get; set; } = spec.FeeCollector;
        public ulong Eip4844TransitionTimestamp { get; set; } = spec.Eip4844TransitionTimestamp;
        public ulong TargetBlobCount { get; set; } = spec.TargetBlobCount;
        public ulong MaxBlobCount { get; set; } = spec.MaxBlobCount;
        public ulong MaxBlobsPerTx { get; set; } = spec.MaxBlobsPerTx;
        public UInt256 BlobBaseFeeUpdateFraction { get; set; } = spec.BlobBaseFeeUpdateFraction;
        public bool IsEip1153Enabled { get; set; } = spec.IsEip1153Enabled;
        public bool IsEip3651Enabled { get; set; } = spec.IsEip3651Enabled;
        public bool IsEip3855Enabled { get; set; } = spec.IsEip3855Enabled;
        public bool IsEip3860Enabled { get; set; } = spec.IsEip3860Enabled;
        public bool IsEip4895Enabled { get; set; } = spec.IsEip4895Enabled;
        public ulong WithdrawalTimestamp { get; set; } = spec.WithdrawalTimestamp;
        public bool IsEip5656Enabled { get; set; } = spec.IsEip5656Enabled;
        public long Eip2935RingBufferSize { get; set; } = spec.Eip2935RingBufferSize;
        public bool IsEip6780Enabled { get; set; } = spec.IsEip6780Enabled;
        public bool IsEip4788Enabled { get; set; } = spec.IsEip4788Enabled;
        public bool IsEip4844FeeCollectorEnabled { get; set; } = spec.IsEip4844FeeCollectorEnabled;
        public Address? Eip4788ContractAddress { get; set; } = spec.Eip4788ContractAddress;
        public bool IsEip7002Enabled { get; set; } = spec.IsEip7002Enabled;
        public Address? Eip7002ContractAddress { get; set; } = spec.Eip7002ContractAddress;
        public bool IsEip7251Enabled { get; set; } = spec.IsEip7251Enabled;
        public Address? Eip7251ContractAddress { get; set; } = spec.Eip7251ContractAddress;
        public bool IsEip2935Enabled { get; set; } = spec.IsEip2935Enabled;
        public bool IsEip7709Enabled { get; set; } = spec.IsEip7709Enabled;
        public Address? Eip2935ContractAddress { get; set; } = spec.Eip2935ContractAddress;
        public bool IsEip7702Enabled { get; set; } = spec.IsEip7702Enabled;
        public bool IsEip7823Enabled { get; set; } = spec.IsEip7823Enabled;
        public bool IsEip7825Enabled { get; set; } = spec.IsEip7825Enabled;
        public UInt256 ForkBaseFee { get; set; } = spec.ForkBaseFee;
        public UInt256 BaseFeeMaxChangeDenominator { get; set; } = spec.BaseFeeMaxChangeDenominator;
        public long ElasticityMultiplier { get; set; } = spec.ElasticityMultiplier;
        public IBaseFeeCalculator BaseFeeCalculator { get; set; } = spec.BaseFeeCalculator;
        public bool IsEofEnabled { get; set; } = spec.IsEofEnabled;
        public bool IsEip6110Enabled { get; set; } = spec.IsEip6110Enabled;
        public Address? DepositContractAddress { get; set; } = spec.DepositContractAddress;
        public bool IsEip7594Enabled { get; set; } = spec.IsEip7594Enabled;
        Array? IReleaseSpec.EvmInstructionsNoTrace { get => spec.EvmInstructionsNoTrace; set => spec.EvmInstructionsNoTrace = value; }
        Array? IReleaseSpec.EvmInstructionsTraced { get => spec.EvmInstructionsTraced; set => spec.EvmInstructionsTraced = value; }
        public bool IsEip7939Enabled { get; set; } = spec.IsEip7939Enabled;
        public bool IsEip7907Enabled { get; set; } = spec.IsEip7907Enabled;
        public bool IsRip7728Enabled { get; set; } = spec.IsRip7728Enabled;
        public bool IsEip7928Enabled { get; set; } = spec.IsEip7928Enabled;
        public bool IsEip7778Enabled { get; set; } = spec.IsEip7778Enabled;
        public bool IsEip7843Enabled => spec.IsEip7843Enabled;
        FrozenSet<AddressAsKey> IReleaseSpec.Precompiles => spec.Precompiles;
    }
}
