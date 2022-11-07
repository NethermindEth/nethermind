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
// 

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
            MaximumExtraDataSize = spec.MaximumExtraDataSize                   ;
            MaxCodeSize  = spec.MaxCodeSize ;
            MinGasLimit  = spec.MinGasLimit ;
            GasLimitBoundDivisor = spec.GasLimitBoundDivisor;
            BlockReward  = spec.BlockReward ;
            DifficultyBombDelay = spec.DifficultyBombDelay;
            DifficultyBoundDivisor  = spec.DifficultyBoundDivisor ;
            FixedDifficulty  = spec.FixedDifficulty ;
            MaximumUncleCount = spec.MaximumUncleCount;
            IsTimeAdjustmentPostOlympic = spec.IsTimeAdjustmentPostOlympic;
            IsEip2Enabled  = spec.IsEip2Enabled ;
            IsEip7Enabled  = spec.IsEip7Enabled ;
            IsEip100Enabled  = spec.IsEip100Enabled ;
            IsEip140Enabled  = spec.IsEip140Enabled ;
            IsEip150Enabled  = spec.IsEip150Enabled ;
            IsEip155Enabled  = spec.IsEip155Enabled ;
            IsEip158Enabled  = spec.IsEip158Enabled ;
            IsEip160Enabled  = spec.IsEip160Enabled ;
            IsEip170Enabled  = spec.IsEip170Enabled ;
            IsEip196Enabled  = spec.IsEip196Enabled ;
            IsEip197Enabled  = spec.IsEip197Enabled ;
            IsEip198Enabled  = spec.IsEip198Enabled ;
            IsEip211Enabled  = spec.IsEip211Enabled ;
            IsEip214Enabled  = spec.IsEip214Enabled ;
            IsEip649Enabled  = spec.IsEip649Enabled ;
            IsEip658Enabled  = spec.IsEip658Enabled ;
            IsEip145Enabled  = spec.IsEip145Enabled ;
            IsEip1014Enabled  = spec.IsEip1014Enabled ;
            IsEip1052Enabled  = spec.IsEip1052Enabled ;
            IsEip1283Enabled  = spec.IsEip1283Enabled ;
            IsEip1234Enabled  = spec.IsEip1234Enabled ;
            IsEip1344Enabled  = spec.IsEip1344Enabled ;
            IsEip2028Enabled  = spec.IsEip2028Enabled ;
            IsEip152Enabled  = spec.IsEip152Enabled ;
            IsEip1108Enabled  = spec.IsEip1108Enabled ;
            IsEip1884Enabled  = spec.IsEip1884Enabled ;
            IsEip2200Enabled  = spec.IsEip2200Enabled ;
            IsEip2315Enabled  = spec.IsEip2315Enabled ;
            IsEip2537Enabled  = spec.IsEip2537Enabled ;
            IsEip2565Enabled  = spec.IsEip2565Enabled ;
            IsEip2929Enabled  = spec.IsEip2929Enabled ;
            IsEip2930Enabled  = spec.IsEip2930Enabled ;
            IsEip1559Enabled  = spec.IsEip1559Enabled ;
            IsEip3198Enabled  = spec.IsEip3198Enabled ;
            IsEip3529Enabled  = spec.IsEip3529Enabled ;
            IsEip3541Enabled  = spec.IsEip3541Enabled ;
            IsEip3607Enabled  = spec.IsEip3607Enabled ;
            IsEip3675Enabled  = spec.IsEip3675Enabled ;
            IsEip3651Enabled  = spec.IsEip3651Enabled ;
            IsEip1153Enabled  = spec.IsEip1153Enabled ;
            IsEip3855Enabled  = spec.IsEip3855Enabled ;
            IsEip3860Enabled  = spec.IsEip3860Enabled ;
            IsEip3540Enabled  = spec.IsEip3540Enabled ;
            IsEip3670Enabled  = spec.IsEip3670Enabled ;
            IsEip4200Enabled  = spec.IsEip4200Enabled ;
            Eip1559TransitionBlock = spec.Eip1559TransitionBlock;
            IsEip4750Enabled  = spec.IsEip4750Enabled;
        }

        public string Name => "OverridableReleaseSpec";

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

        public bool IsEip1559Enabled { get; set; }

        public bool IsEip3198Enabled { get; set; }

        public bool IsEip3529Enabled { get; set; }

        public bool IsEip3541Enabled { get; set; }

        public bool IsEip3607Enabled { get; set; }

        public bool IsEip3675Enabled { get; set; }

        public bool IsEip3651Enabled { get; set; }

        public bool IsEip1153Enabled { get; set; }

        public bool IsEip3855Enabled { get; set; }

        public bool IsEip3860Enabled { get; set; }

        public bool IsEip3540Enabled { get; set; }

        public bool IsEip3670Enabled { get; set; }

        public bool IsEip4200Enabled { get; set; }

        public long Eip1559TransitionBlock { get; set; }
        public bool IsEip4750Enabled { get; set; }

        public bool IsEip158IgnoredAccount(Address address)
            => _spec.IsEip158IgnoredAccount(address);
    }
}
