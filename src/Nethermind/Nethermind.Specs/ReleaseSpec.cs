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
        
        public bool IsEip3541Enabled { get; set; }
        public bool ValidateChainId { get; set; }
        public bool ValidateReceipts { get; set; }
        public long Eip1559TransitionBlock { get; set; }
    }
}
