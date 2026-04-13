// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa
{
    public class AuraDifficultyCalculator(IAuRaStepCalculator auRaStepCalculator) : IDifficultyCalculator
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator = auRaStepCalculator;
        public static readonly UInt256 MaxDifficulty;

        static AuraDifficultyCalculator() => MaxDifficulty = UInt256.UInt128MaxValue;

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
