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

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs
{
    public class ReleaseSpec : IReleaseSpec
    {
        public ReleaseSpec() { }
        public ReleaseSpec(IReleaseSpec sourceSpec)
        {
            MaximumExtraDataSize = sourceSpec.MaximumExtraDataSize;
            MaxCodeSize = sourceSpec.MaxCodeSize;
            MinGasLimit = sourceSpec.MinGasLimit;
            GasLimitBoundDivisor = sourceSpec.GasLimitBoundDivisor;
            BlockReward = sourceSpec.BlockReward;
            DifficultyBombDelay = sourceSpec.DifficultyBombDelay;
            DifficultyBoundDivisor = sourceSpec.DifficultyBoundDivisor;
            FixedDifficulty = sourceSpec.FixedDifficulty;
            MaximumUncleCount = sourceSpec.MaximumUncleCount;
            IsTimeAdjustmentPostOlympic = sourceSpec.IsTimeAdjustmentPostOlympic;
            IsEip2Enabled = sourceSpec.IsEip2Enabled;
            IsEip7Enabled = sourceSpec.IsEip7Enabled;
            IsEip100Enabled = sourceSpec.IsEip100Enabled;
            IsEip140Enabled = sourceSpec.IsEip140Enabled;
            IsEip150Enabled = sourceSpec.IsEip150Enabled;
            IsEip155Enabled = sourceSpec.IsEip155Enabled;
            IsEip158Enabled = sourceSpec.IsEip158Enabled;
            IsEip160Enabled = sourceSpec.IsEip160Enabled;
            IsEip170Enabled = sourceSpec.IsEip170Enabled;
            IsEip196Enabled = sourceSpec.IsEip196Enabled;
            IsEip197Enabled = sourceSpec.IsEip197Enabled;
            IsEip198Enabled = sourceSpec.IsEip198Enabled;
            IsEip211Enabled = sourceSpec.IsEip211Enabled;
            IsEip214Enabled = sourceSpec.IsEip214Enabled;
            IsEip649Enabled = sourceSpec.IsEip649Enabled;
            IsEip658Enabled = sourceSpec.IsEip658Enabled;
            IsEip145Enabled = sourceSpec.IsEip145Enabled;
            IsEip1014Enabled = sourceSpec.IsEip1014Enabled;
            IsEip1052Enabled = sourceSpec.IsEip1052Enabled;
            IsEip1283Enabled = sourceSpec.IsEip1283Enabled;
            IsEip1234Enabled = sourceSpec.IsEip1234Enabled;
            IsEip1344Enabled = sourceSpec.IsEip1344Enabled;
            IsEip2028Enabled = sourceSpec.IsEip2028Enabled;
            IsEip152Enabled = sourceSpec.IsEip152Enabled;
            IsEip1108Enabled = sourceSpec.IsEip1108Enabled;
            IsEip1884Enabled = sourceSpec.IsEip1884Enabled;
            IsEip2200Enabled = sourceSpec.IsEip2200Enabled;
            IsEip2315Enabled = sourceSpec.IsEip2315Enabled;
            IsEip2537Enabled = sourceSpec.IsEip2537Enabled;
            IsEip2565Enabled = sourceSpec.IsEip2565Enabled;
            IsEip2929Enabled = sourceSpec.IsEip2929Enabled;
            IsEip2930Enabled = sourceSpec.IsEip2930Enabled;
            IsEip1559Enabled = sourceSpec.IsEip1559Enabled;
            IsEip3198Enabled = sourceSpec.IsEip3198Enabled;
            IsEip3529Enabled = sourceSpec.IsEip3529Enabled;
            IsEip3607Enabled = sourceSpec.IsEip3607Enabled;
            IsEip3541Enabled = sourceSpec.IsEip3541Enabled;
            ValidateChainId = sourceSpec.ValidateChainId;
            ValidateReceipts = sourceSpec.ValidateReceipts;
            Eip1559TransitionBlock = sourceSpec.Eip1559TransitionBlock;
            Eip1559FeeCollector = sourceSpec.Eip1559FeeCollector;
            Eip1559BaseFeeMinValue = sourceSpec.Eip1559BaseFeeMinValue;
            IsEip1153Enabled = sourceSpec.IsEip1153Enabled;
            IsEip3540Enabled = sourceSpec.IsEip3540Enabled;
            IsEip3670Enabled = sourceSpec.IsEip3670Enabled;
        }
        public string Name => "Custom";
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
        public bool IsEip2315Enabled { get; set; }
        public bool IsEip2537Enabled { get; set; }
        public bool IsEip2565Enabled { get; set; }
        public bool IsEip2929Enabled { get; set; }
        public bool IsEip2930Enabled { get; set; }
        public bool IsEip158IgnoredAccount(Address address) => address == Address.SystemUser;
        public bool IsEip1559Enabled { get; set; }
        public bool IsEip3198Enabled { get; set; }
        public bool IsEip3529Enabled { get; set; }
        public bool IsEip3607Enabled { get; set; }
        public bool IsEip3541Enabled { get; set; }
        public bool ValidateChainId { get; set; }
        public bool ValidateReceipts { get; set; }
        public long Eip1559TransitionBlock { get; set; }
        public Address Eip1559FeeCollector { get; set; }
        public UInt256? Eip1559BaseFeeMinValue { get; set; }
        public bool IsEip1153Enabled { get; set; }
        public bool IsEip3540Enabled { get; set; }
        public bool IsEip3670Enabled { get; set; }
    }
}
