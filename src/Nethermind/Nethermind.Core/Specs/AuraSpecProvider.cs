// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core.Specs;

public class AuraSpecProvider(IReleaseSpec spec) : IReleaseSpec
{
    public bool IsEip1559Enabled => spec.IsEip1559Enabled;

    public long Eip1559TransitionBlock => spec.Eip1559TransitionBlock;

    public UInt256 ForkBaseFee => spec.ForkBaseFee;

    public UInt256 BaseFeeMaxChangeDenominator => spec.BaseFeeMaxChangeDenominator;

    public long ElasticityMultiplier => spec.ElasticityMultiplier;

    public bool IsEip658Enabled => spec.IsEip658Enabled;

    public string Name => spec.Name;

    public long MaximumExtraDataSize => spec.MaximumExtraDataSize;

    public long MaxCodeSize => spec.MaxCodeSize;

    public long MinGasLimit => spec.MinGasLimit;

    public long GasLimitBoundDivisor => spec.GasLimitBoundDivisor;

    public UInt256 BlockReward => spec.BlockReward;

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

    public bool IsEip3198Enabled => spec.IsEip3198Enabled;

    public bool IsEip3529Enabled => spec.IsEip3529Enabled;

    public bool IsEip3541Enabled => spec.IsEip3541Enabled;

    public bool IsEip3607Enabled => spec.IsEip3607Enabled;

    public bool IsEip3651Enabled => spec.IsEip3651Enabled;

    public bool IsEip1153Enabled => spec.IsEip1153Enabled;

    public bool IsEip3855Enabled => spec.IsEip3855Enabled;

    public bool IsEip5656Enabled => spec.IsEip5656Enabled;

    public bool IsEip3860Enabled => spec.IsEip3860Enabled;

    public bool IsEip4895Enabled => spec.IsEip4895Enabled;

    public bool IsEip4844Enabled => spec.IsEip4844Enabled;

    public bool IsEip4788Enabled => spec.IsEip4788Enabled;

    public Address Eip4788ContractAddress => spec.Eip4788ContractAddress;

    public bool IsEip2935Enabled => spec.IsEip2935Enabled;

    public bool IsEip7709Enabled => spec.IsEip7709Enabled;

    public Address Eip2935ContractAddress => spec.Eip2935ContractAddress;

    public bool IsEip6780Enabled => spec.IsEip6780Enabled;

    public bool IsRip7212Enabled => spec.IsRip7212Enabled;

    public ulong WithdrawalTimestamp => spec.WithdrawalTimestamp;

    public ulong Eip4844TransitionTimestamp => spec.Eip4844TransitionTimestamp;

    public bool IsEip158IgnoredAccount(Address address) => address == Address.SystemUser;
}
