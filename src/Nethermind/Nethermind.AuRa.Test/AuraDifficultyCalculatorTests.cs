// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Consensus.AuRa;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuraDifficultyCalculatorTests
    {
        private static IEnumerable DifficultyTestCases
        {
            get
            {
                yield return new TestCaseData(1UL, 0UL, 0UL).Returns(UInt256.UInt128MaxValue - 1);
                yield return new TestCaseData(10UL, 0UL, 0UL).Returns(UInt256.UInt128MaxValue - 10);
                yield return new TestCaseData(10UL, 9UL, 0UL).Returns(UInt256.UInt128MaxValue - 1);
                yield return new TestCaseData(100UL, 10UL, 0UL).Returns(UInt256.UInt128MaxValue - 90);

                yield return new TestCaseData(1UL, 0UL, 1UL).Returns(UInt256.UInt128MaxValue);
                yield return new TestCaseData(10UL, 0UL, 5UL).Returns(UInt256.UInt128MaxValue - 5);
                yield return new TestCaseData(10UL, 9UL, 3UL).Returns(new UInt256(1, 0, 1, 0));
                yield return new TestCaseData(100UL, 10UL, 10UL).Returns(UInt256.UInt128MaxValue - 80);
            }
        }

        [TestCaseSource(nameof(DifficultyTestCases))]
        public UInt256 calculates_difficulty(ulong step, ulong parentStep, ulong emptyStepCount) =>
            AuraDifficultyCalculator.CalculateDifficulty(parentStep, step, emptyStepCount);
    }
}
