// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs
{
    public class ReleaseSpec : IReleaseSpec
    {
        public string Name { get; set; } = "Custom";
        public long MaximumExtraDataSize { get; set; }
        public long MaxCodeSize { get; set; }
        public long MinGasLimit { get; set; }
        public long GasLimitBoundDivisor { get; set; }
        public UInt256 BlockReward { get; set; }
        public long DifficultyBombDelay { get; set; }
        public long DifficultyBoundDivisor { get; set; }
        public long? FixedDifficulty { get; set; }
        public int MaximumUncleCount { get; set; }
        public bool IsTimeAdjustmentPostOlympic { get; set; }
        public bool IsEip2Enabled { get; set; }
        public bool IsEip7Enabled { get; set; }
        public bool IsEip100Enabled { get; set; }
        public bool IsEip140Enabled { get; set; }
        public bool IsEip150Enabled { get; set; }
        public bool IsEip155Enabled { get; set; }
        public bool IsEip158Enabled { get; set; }
        public bool IsEip160Enabled { get; set; }
        public bool IsEip170Enabled { get; set; }
        public bool IsEip196Enabled { get; set; }
        public bool IsEip197Enabled { get; set; }
        public bool IsEip198Enabled { get; set; }
        public bool IsEip211Enabled { get; set; }
        public bool IsEip214Enabled { get; set; }
        public bool IsEip649Enabled { get; set; }
        public bool IsEip658Enabled { get; set; }
        public bool IsEip145Enabled { get; set; }
        public bool IsEip1014Enabled { get; set; }
        public bool IsEip1052Enabled { get; set; }
        public bool IsEip1283Enabled { get; set; }
        public bool IsEip1234Enabled { get; set; }
        public bool IsEip1344Enabled { get; set; }
        public bool IsEip2028Enabled { get; set; }
        public bool IsEip152Enabled { get; set; }
        public bool IsEip1108Enabled { get; set; }
        public bool IsEip1884Enabled { get; set; }
        public bool IsEip2200Enabled { get; set; }
        public bool IsEip2537Enabled { get; set; }
        public bool IsEip2565Enabled { get; set; }
        public bool IsEip2929Enabled { get; set; }
        public bool IsEip2930Enabled { get; set; }

        // used only in testing
        public ReleaseSpec Clone() => (ReleaseSpec)MemberwiseClone();

        public bool IsEip1559Enabled
        {
            get => _isEip1559Enabled || IsEip4844Enabled;
            set => _isEip1559Enabled = value;
        }

        public bool IsEip3198Enabled { get; set; }
        public bool IsEip3529Enabled { get; set; }
        public bool IsEip3607Enabled { get; set; }
        public bool IsEip3541Enabled { get; set; }
        public bool ValidateChainId { get; set; }
        public bool ValidateReceipts { get; set; }
        public long Eip1559TransitionBlock { get; set; }
        public ulong WithdrawalTimestamp { get; set; }
        public ulong Eip4844TransitionTimestamp { get; set; }
        public Address FeeCollector { get; set; }
        public UInt256? Eip1559BaseFeeMinValue { get; set; }
        public UInt256 ForkBaseFee { get; set; } = Eip1559Constants.DefaultForkBaseFee;
        public UInt256 BaseFeeMaxChangeDenominator { get; set; } = Eip1559Constants.DefaultBaseFeeMaxChangeDenominator;
        public long ElasticityMultiplier { get; set; } = Eip1559Constants.DefaultElasticityMultiplier;
        public IBaseFeeCalculator BaseFeeCalculator { get; set; } = new DefaultBaseFeeCalculator();
        public bool IsEip1153Enabled { get; set; }
        public bool IsEip3651Enabled { get; set; }
        public bool IsEip3855Enabled { get; set; }
        public bool IsEip3860Enabled { get; set; }
        public bool IsEip4895Enabled { get; set; }
        public bool IsEip4844Enabled { get; set; }
        public bool IsRip7212Enabled { get; set; }
        public bool IsOpGraniteEnabled { get; set; }
        public bool IsOpHoloceneEnabled { get; set; }
        public bool IsOpIsthmusEnabled { get; set; }
        public bool IsEip7623Enabled { get; set; }
        public bool IsEip7883Enabled { get; set; }
        public bool IsEip5656Enabled { get; set; }
        public bool IsEip6780Enabled { get; set; }
        public bool IsEip4788Enabled { get; set; }
        public bool IsEip7702Enabled { get; set; }
        public bool IsEip7823Enabled { get; set; }
        public bool IsEip4844FeeCollectorEnabled { get; set; }
        public bool IsEip7002Enabled { get; set; }
        public bool IsEip7251Enabled { get; set; }
        public bool IsEip7825Enabled { get; set; }
        public bool IsEip7918Enabled { get; set; }

        public ulong TargetBlobCount { get; set; }
        public ulong MaxBlobCount { get; set; }
        public UInt256 BlobBaseFeeUpdateFraction { get; set; }


        private Address _eip7251ContractAddress;
        public Address Eip7251ContractAddress
        {
            get => IsEip7251Enabled ? _eip7251ContractAddress : null;
            set => _eip7251ContractAddress = value;
        }
        private Address _eip7002ContractAddress;
        public Address Eip7002ContractAddress
        {
            get => IsEip7002Enabled ? _eip7002ContractAddress : null;
            set => _eip7002ContractAddress = value;
        }

        private Address _eip4788ContractAddress;
        public Address Eip4788ContractAddress
        {
            get => IsEip4788Enabled ? _eip4788ContractAddress : null;
            set => _eip4788ContractAddress = value;
        }

        public bool IsEofEnabled { get; set; }

        public bool IsEip6110Enabled { get; set; }

        private Address _depositContractAddress;
        public Address DepositContractAddress
        {
            get => IsEip6110Enabled ? _depositContractAddress : null;
            set => _depositContractAddress = value;
        }
        public bool IsEip2935Enabled { get; set; }
        public bool IsEip7709Enabled { get; set; }

        private Address _eip2935ContractAddress;
        private bool _isEip1559Enabled;

        public Address Eip2935ContractAddress
        {
            get => IsEip2935Enabled ? _eip2935ContractAddress : null;
            set => _eip2935ContractAddress = value;
        }

        public bool IsEip7594Enabled { get; set; }

        Array? IReleaseSpec.EvmInstructionsNoTrace { get; set; }

        Array? IReleaseSpec.EvmInstructionsTraced { get; set; }
    }
}
