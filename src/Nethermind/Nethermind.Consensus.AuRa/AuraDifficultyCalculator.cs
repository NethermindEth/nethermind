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

using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Consensus.AuRa
{
    public static class AuraDifficultyCalculator
    {
        public static readonly UInt256 MaxDifficulty;

        static AuraDifficultyCalculator()
        {
            UInt256.Create(out MaxDifficulty, UInt128.MaxValue, UInt128.Zero);
        }

        public static UInt256 CalculateDifficulty(long parentStep, long currentStep, long emptyStepsCount = 0L) =>
            MaxDifficulty + (UInt256)parentStep - (UInt256)currentStep + (UInt256)emptyStepsCount;
    }
}