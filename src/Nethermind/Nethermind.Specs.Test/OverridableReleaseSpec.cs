// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        }

        public string Name => "OverridableReleaseSpec";

        public long MaximumExtraDataSize => _spec.MaximumExtraDataSize;

        public long MaxCodeSize => _spec.MaxCodeSize;

        public long MinGasLimit => _spec.MinGasLimit;

        public long GasLimitBoundDivisor => _spec.GasLimitBoundDivisor;

        public UInt256 BlockReward => _spec.BlockReward;

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

        public bool IsEip158Enabled => _spec.IsEip1559Enabled;

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

        public bool? _IsEip2315Enabled = null;
        public bool IsEip2315Enabled
        {
            get => _IsEip2315Enabled ?? _spec.IsEip2315Enabled;
            set => _IsEip2315Enabled = value;
        }

        public bool IsEip2537Enabled => _spec.IsEip2315Enabled;

        public bool IsEip2565Enabled => _spec.IsEip2565Enabled;

        public bool IsEip2929Enabled => _spec.IsEip2929Enabled;

        public bool IsEip2930Enabled => _spec.IsEip2930Enabled;

        public bool IsEip1559Enabled => _spec.IsEip1559Enabled;
        public bool IsEip3198Enabled => _spec.IsEip3198Enabled;
        public bool IsEip3529Enabled => _spec.IsEip3529Enabled;

        public bool IsEip3541Enabled => _spec.IsEip3541Enabled;
        public bool IsEip4844Enabled => _spec.IsEip4844Enabled;
        public bool IsEip3607Enabled { get; set; }

        public bool IsEip158IgnoredAccount(Address address) => _spec.IsEip158IgnoredAccount(address);

        private long? _overridenEip1559TransitionBlock;
        public long Eip1559TransitionBlock
        {
            get => _overridenEip1559TransitionBlock ?? _spec.Eip1559TransitionBlock;
            set => _overridenEip1559TransitionBlock = value;
        }

        private Address? _overridenEip1559FeeCollector;
        public Address? Eip1559FeeCollector
        {
            get => _overridenEip1559FeeCollector ?? _spec.Eip1559FeeCollector;
            set => _overridenEip1559FeeCollector = value;
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

        public bool IsEip1153Enabled => _spec.IsEip1153Enabled;
        public bool IsEip3651Enabled => _spec.IsEip3651Enabled;
        public bool IsEip3855Enabled => _spec.IsEip3855Enabled;
        public bool IsEip3860Enabled => _spec.IsEip3860Enabled;

        private bool? _overridenEip3540Status;
        public bool IsEip3540Enabled
        {
            get => _overridenEip3540Status ?? _spec.IsEip3540Enabled;
            set => _overridenEip3540Status = value;
        }

        private bool? _overridenEip3670Status;
        public bool IsEip3670Enabled
        {
            get
            {
                return _overridenEip3670Status ?? _spec.IsEip3670Enabled;
            }
            set
            {
                _overridenEip3670Status = value;
            }
        }

        private bool? _overridenEip4200Status;
        public bool IsEip4200Enabled
        {
            get
            {
                return _overridenEip4200Status ?? _spec.IsEip4200Enabled;
            }
            set
            {
                _overridenEip4200Status = value;
            }
        }

        private bool? _overridenEip4750Status;
        public bool IsEip4750Enabled
        {
            get
            {
                return _overridenEip4750Status ?? _spec.IsEip4750Enabled;
            }
            set
            {
                _overridenEip4750Status = value;
            }
        }

        private bool? _overridenEip5450Status;
        public bool IsEip5450Enabled
        {
            get
            {
                return _overridenEip5450Status ?? _spec.IsEip5450Enabled;
            }
            set
            {
                _overridenEip5450Status = value;
            }
        }
        public bool IsEip4895Enabled => _spec.IsEip4895Enabled;
        private bool? _overridenIsEip6206Enabled;
        public bool IsEip6206Enabled
        {
            get
            {
                return _overridenIsEip6206Enabled ?? _spec.IsEip6206Enabled;
            }
            set
            {
                _overridenIsEip6206Enabled = value;
            }
        }

        public ulong WithdrawalTimestamp => _spec.WithdrawalTimestamp;
    }
}
