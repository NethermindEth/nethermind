using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Taiko;

public class TaikoAnchorTxReleaseSpec(IReleaseSpec parent, Address? eip1559FeeCollector) : IReleaseSpec
{
    private readonly IReleaseSpec _parent = parent;
    private readonly Address? eip1559FeeCollector = eip1559FeeCollector;

    public string Name => _parent.Name;

    public long MaximumExtraDataSize => _parent.MaximumExtraDataSize;

    public long MaxCodeSize => _parent.MaxCodeSize;

    public long MinGasLimit => _parent.MinGasLimit;

    public long GasLimitBoundDivisor => _parent.GasLimitBoundDivisor;

    public UInt256 BlockReward => _parent.BlockReward;

    public long DifficultyBombDelay => _parent.DifficultyBombDelay;

    public long DifficultyBoundDivisor => _parent.DifficultyBoundDivisor;

    public long? FixedDifficulty => _parent.FixedDifficulty;

    public int MaximumUncleCount => _parent.MaximumUncleCount;

    public bool IsTimeAdjustmentPostOlympic => _parent.IsTimeAdjustmentPostOlympic;

    public bool IsEip2Enabled => _parent.IsEip2Enabled;

    public bool IsEip7Enabled => _parent.IsEip7Enabled;

    public bool IsEip100Enabled => _parent.IsEip100Enabled;

    public bool IsEip140Enabled => _parent.IsEip140Enabled;

    public bool IsEip150Enabled => _parent.IsEip150Enabled;

    public bool IsEip155Enabled => _parent.IsEip155Enabled;

    public bool IsEip158Enabled => _parent.IsEip158Enabled;

    public bool IsEip160Enabled => _parent.IsEip160Enabled;

    public bool IsEip170Enabled => _parent.IsEip170Enabled;

    public bool IsEip196Enabled => _parent.IsEip196Enabled;

    public bool IsEip197Enabled => _parent.IsEip197Enabled;

    public bool IsEip198Enabled => _parent.IsEip198Enabled;

    public bool IsEip211Enabled => _parent.IsEip211Enabled;

    public bool IsEip214Enabled => _parent.IsEip214Enabled;

    public bool IsEip649Enabled => _parent.IsEip649Enabled;

    public bool IsEip145Enabled => _parent.IsEip145Enabled;

    public bool IsEip1014Enabled => _parent.IsEip1014Enabled;

    public bool IsEip1052Enabled => _parent.IsEip1052Enabled;

    public bool IsEip1283Enabled => _parent.IsEip1283Enabled;

    public bool IsEip1234Enabled => _parent.IsEip1234Enabled;

    public bool IsEip1344Enabled => _parent.IsEip1344Enabled;

    public bool IsEip2028Enabled => _parent.IsEip2028Enabled;

    public bool IsEip152Enabled => _parent.IsEip152Enabled;

    public bool IsEip1108Enabled => _parent.IsEip1108Enabled;

    public bool IsEip1884Enabled => _parent.IsEip1884Enabled;

    public bool IsEip2200Enabled => _parent.IsEip2200Enabled;

    public bool IsEip2537Enabled => _parent.IsEip2537Enabled;

    public bool IsEip2565Enabled => _parent.IsEip2565Enabled;

    public bool IsEip2929Enabled => _parent.IsEip2929Enabled;

    public bool IsEip2930Enabled => _parent.IsEip2930Enabled;

    public bool IsEip3198Enabled => _parent.IsEip3198Enabled;

    public bool IsEip3529Enabled => _parent.IsEip3529Enabled;

    public bool IsEip3541Enabled => _parent.IsEip3541Enabled;

    public bool IsEip3607Enabled => _parent.IsEip3607Enabled;

    public bool IsEip3651Enabled => _parent.IsEip3651Enabled;

    public bool IsEip1153Enabled => _parent.IsEip1153Enabled;

    public bool IsEip3855Enabled => _parent.IsEip3855Enabled;

    public bool IsEip5656Enabled => _parent.IsEip5656Enabled;

    public bool IsEip3860Enabled => _parent.IsEip3860Enabled;

    public bool IsEip4895Enabled => _parent.IsEip4895Enabled;

    public bool IsEip4844Enabled => _parent.IsEip4844Enabled;

    public bool IsEip4788Enabled => _parent.IsEip4788Enabled;

    public Address Eip4788ContractAddress => _parent.Eip4788ContractAddress;

    public bool IsEip2935Enabled => _parent.IsEip2935Enabled;

    public bool IsEip7709Enabled => _parent.IsEip7709Enabled;

    public Address Eip2935ContractAddress => _parent.Eip2935ContractAddress;

    public bool IsEip6780Enabled => _parent.IsEip6780Enabled;

    public bool IsRip7212Enabled => _parent.IsRip7212Enabled;

    public ulong WithdrawalTimestamp => _parent.WithdrawalTimestamp;

    public ulong Eip4844TransitionTimestamp => _parent.Eip4844TransitionTimestamp;

    public bool IsEip1559Enabled => _parent.IsEip1559Enabled;

    public long Eip1559TransitionBlock => _parent.Eip1559TransitionBlock;

    public UInt256 ForkBaseFee => _parent.ForkBaseFee;

    public UInt256 BaseFeeMaxChangeDenominator => _parent.BaseFeeMaxChangeDenominator;

    public long ElasticityMultiplier => _parent.ElasticityMultiplier;

    public bool IsEip658Enabled => _parent.IsEip658Enabled;

    public bool IsEip158IgnoredAccount(Address address) => _parent.IsEip158IgnoredAccount(address);

    public Address? Eip1559FeeCollector => eip1559FeeCollector;

    public UInt256? Eip1559BaseFeeMinValue => _parent.Eip1559BaseFeeMinValue;

}
