// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
                yield return new TestCaseData(1, 0, 0).Returns(UInt256.UInt128MaxValue - 1);
                yield return new TestCaseData(10, 0, 0).Returns(UInt256.UInt128MaxValue - 10);
                yield return new TestCaseData(10, 9, 0).Returns(UInt256.UInt128MaxValue - 1);
                yield return new TestCaseData(100, 10, 0).Returns(UInt256.UInt128MaxValue - 90);

                yield return new TestCaseData(1, 0, 1).Returns(UInt256.UInt128MaxValue);
                yield return new TestCaseData(10, 0, 5).Returns(UInt256.UInt128MaxValue - 5);
                yield return new TestCaseData(10, 9, 3).Returns(new UInt256(1, 0, 1, 0));
                yield return new TestCaseData(100, 10, 10).Returns(UInt256.UInt128MaxValue - 80);
            }
        }

        [TestCaseSource(nameof(DifficultyTestCases))]
        public UInt256 calculates_difficulty(long step, long parentStep, long emptyStepCount) =>
            AuraDifficultyCalculator.CalculateDifficulty(parentStep, step, emptyStepCount);
    }
}
