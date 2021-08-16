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

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Forks
{
    public class London : IReleaseSpec
    {
        private static IReleaseSpec _instance;

        private London()
        {
        }

        public static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new London());

        public string Name => "London";
        public long MaximumExtraDataSize => 32;
        public long MaxCodeSize => 24576;
        public long MinGasLimit => 5000;
        public long GasLimitBoundDivisor => 0x0400;
        public UInt256 BlockReward { get; } = UInt256.Parse("2000000000000000000");
        public long DifficultyBombDelay => 9700000L;
        public long DifficultyBoundDivisor => 0x0800;
        public long? FixedDifficulty => null;
        public int MaximumUncleCount => 2;
        public bool IsTimeAdjustmentPostOlympic => true;
        public bool IsEip2Enabled => true;
        public bool IsEip7Enabled => true;
        public bool IsEip100Enabled => true;
        public bool IsEip140Enabled => true;
        public bool IsEip150Enabled => true;
        public bool IsEip155Enabled => true;
        public bool IsEip158Enabled => true;
        public bool IsEip160Enabled => true;
        public bool IsEip170Enabled => true;
        public bool IsEip196Enabled => true;
        public bool IsEip197Enabled => true;
        public bool IsEip198Enabled => true;
        public bool IsEip211Enabled => true;
        public bool IsEip214Enabled => true;
        public bool IsEip649Enabled => true;
        public bool IsEip658Enabled => true;
        public bool IsEip145Enabled => true;
        public bool IsEip1014Enabled => true;
        public bool IsEip1052Enabled => true;
        public bool IsEip1283Enabled => false;
        public bool IsEip1234Enabled => true;
        public bool IsEip1344Enabled => true;
        public bool IsEip2028Enabled => true;
        public bool IsEip152Enabled => true;
        public bool IsEip1108Enabled => true;
        public bool IsEip1884Enabled => true;
        public bool IsEip2200Enabled => true;
        public bool IsEip2315Enabled => false;
        public bool IsEip2537Enabled => false;
        public bool IsEip2565Enabled => true;
        public bool IsEip2929Enabled => true;
        public bool IsEip2930Enabled => true;
        public bool IsEip158IgnoredAccount(Address address) => false;
        public bool IsEip1559Enabled => true;
        public bool IsEip3198Enabled => true;
        public bool IsEip3529Enabled => true;
        public bool IsEip3541Enabled => true;
        public long Eip1559TransitionBlock => 12965000;
    }
}
