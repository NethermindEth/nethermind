// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa
{
    public class AuraDifficultyCalculator(IAuRaStepCalculator auRaStepCalculator) : IDifficultyCalculator
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator = auRaStepCalculator;
        public static readonly UInt256 MaxDifficulty;

        static AuraDifficultyCalculator() => MaxDifficulty = UInt256.UInt128MaxValue;

        public static UInt256 CalculateDifficulty(ulong parentStep, ulong currentStep, ulong emptyStepsCount = 0UL)
        {
            ulong parentStepWithEmpty = parentStep + emptyStepsCount;
            if (parentStepWithEmpty >= currentStep)
            {
                ulong diff = parentStepWithEmpty - currentStep;
                return MaxDifficulty + (UInt256)diff;
            }
            else
            {
                ulong diff = currentStep - parentStepWithEmpty;
                return MaxDifficulty - (UInt256)diff;
            }
        }

        public UInt256 Calculate(BlockHeader header, BlockHeader parent)
        {
            ulong parentStep = parent.AuRaStep ?? throw new InvalidOperationException("Parent block doesn't have AuRaStep specified.");
            return CalculateDifficulty(parentStep, _auRaStepCalculator.CurrentStep);
        }
    }
}
