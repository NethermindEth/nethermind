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

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Forks
{
    public class Olympic : ReleaseSpec
    {
        private static IReleaseSpec _instance;

        protected Olympic()
        {
            Name = "Olympic";
            MaximumExtraDataSize = 32;
            MaxCodeSize = long.MaxValue;
            MinGasLimit = 5000;
            GasLimitBoundDivisor = 0x0400;
            BlockReward = UInt256.Parse("5000000000000000000");
            DifficultyBoundDivisor = 0x0800;
            IsEip3607Enabled = true;
            MaximumUncleCount = 2;
            Eip1559TransitionBlock = long.MaxValue;
            ValidateChainId = true;
            ValidateReceipts = true;
        }

        public static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Olympic());
        public override bool IsEip158IgnoredAccount(Address address) => false;
    }
}
