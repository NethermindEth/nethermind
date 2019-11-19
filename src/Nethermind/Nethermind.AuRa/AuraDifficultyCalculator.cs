//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.AuRa
{
    public class AuraDifficultyCalculator
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator;
        private static readonly UInt256 maxDifficulty;
        
        static AuraDifficultyCalculator()
        {
            UInt256.Create(out maxDifficulty, UInt128.MaxValue, UInt128.Zero);
        }

        public AuraDifficultyCalculator(IAuRaStepCalculator auRaStepCalculator)
        {
            _auRaStepCalculator = auRaStepCalculator;
        }

        public UInt256 CalculateDifficulty(BlockHeader parent) =>
            maxDifficulty + (UInt256)parent.AuRaStep.Value - (UInt256)_auRaStepCalculator.CurrentStep; // TODO: + empty_steps
        

        public UInt256 MaxDifficulty => maxDifficulty;
    }
}