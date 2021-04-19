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

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle
{
    public class EthashParameters
    {
        public UInt256 MinimumDifficulty { get; set; }

        public long DifficultyBoundDivisor { get; set; }

        public long DurationLimit { get; set; }

        // why is it here??? (this is what chainspec does)
        public long HomesteadTransition { get; set; }

        public long? DaoHardforkTransition { get; set; }

        /// <summary>
        /// This is stored in the Nethermind.Blockchain.DaoData class instead.
        /// </summary>
        public Address DaoHardforkBeneficiary { get; set; }

        /// <summary>
        /// This is stored in the Nethermind.Blockchain.DaoData class instead.
        /// </summary>
        public Address[] DaoHardforkAccounts { get; set; }

        public long Eip100bTransition { get; set; }
        
        public long? FixedDifficulty { get; set; }

        public IDictionary<long, UInt256> BlockRewards { get; set; }

        public IDictionary<long, long> DifficultyBombDelays { get; set; }
    }
}
