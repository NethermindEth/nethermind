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
    public class SystemTransactionReleaseSpec : IReleaseSpec
    {
        private readonly IReleaseSpec _spec;

        public SystemTransactionReleaseSpec(IReleaseSpec spec)
        {
            _spec = spec;
        }

        public string Name => "System";
        
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

        public bool IsEip158Enabled => false;

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

        public bool IsEip2315Enabled => _spec.IsEip2315Enabled;
        
        public bool IsEip2537Enabled => _spec.IsEip2315Enabled;
        
        public bool IsEip2565Enabled => _spec.IsEip2565Enabled;

        public bool IsEip2929Enabled => _spec.IsEip2929Enabled;
        
        public bool IsEip2930Enabled => _spec.IsEip2930Enabled;

        public bool IsEip1559Enabled => _spec.IsEip1559Enabled;
        public bool IsEip3198Enabled => _spec.IsEip3198Enabled;
        public bool IsEip3529Enabled => _spec.IsEip3529Enabled;
        
        public bool IsEip3541Enabled => _spec.IsEip3541Enabled;

        public bool IsEip158IgnoredAccount(Address address)
        {
            return _spec.IsEip158IgnoredAccount(address);
        }

        public long Eip1559TransitionBlock => _spec.Eip1559TransitionBlock;
    }
}
