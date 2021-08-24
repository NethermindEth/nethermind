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
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa
{
    public class AuraDifficultyCalculator : IDifficultyCalculator
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator;
        public static readonly UInt256 MaxDifficulty;

        static AuraDifficultyCalculator()
        {
            MaxDifficulty = UInt256.UInt128MaxValue;
        }

        public AuraDifficultyCalculator(IAuRaStepCalculator auRaStepCalculator)
        {
            _auRaStepCalculator = auRaStepCalculator;
        }

        public static UInt256 CalculateDifficulty(long parentStep, long currentStep, long emptyStepsCount = 0L)
        {
            long mod = parentStep - currentStep + emptyStepsCount;
            if (mod > 0)
            {
                return MaxDifficulty + (UInt256)mod;
            }
            else
            {
                return MaxDifficulty - (UInt256)(-mod);
            }
        }

        public UInt256 Calculate(BlockHeader header, BlockHeader parent) => 
            CalculateDifficulty(parent.AuRaStep.Value, _auRaStepCalculator.CurrentStep);
    }
}
