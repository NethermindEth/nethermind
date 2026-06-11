// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using NUnit.Framework;

namespace Ethereum.Beacon.Test;

/// <summary>
/// Consensus-spec <c>random</c> tests: adversarially randomized block sequences (slashings, skipped
/// slots, exits, deposits) through the full state transition.
/// </summary>
[TestFixture]
public class RandomTests
{
    [TestCaseSource(nameof(RandomCases))]
    public void Random(string casePath) => BeaconStateTestRunner.RunBlocksTest(casePath);

    private static IEnumerable<TestCaseData> RandomCases() =>
        BeaconStateTestRunner.EnumerateCases("fulu", "random", "random");
}
