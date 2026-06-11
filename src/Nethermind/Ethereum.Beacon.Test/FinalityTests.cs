// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using NUnit.Framework;

namespace Ethereum.Beacon.Test;

/// <summary>
/// Consensus-spec <c>finality</c> tests: multi-epoch block sequences whose post states assert the
/// FFG justification and finalization rules.
/// </summary>
[TestFixture]
public class FinalityTests
{
    [TestCaseSource(nameof(FinalityCases))]
    public void Finality(string casePath) => BeaconStateTestRunner.RunBlocksTest(casePath);

    private static IEnumerable<TestCaseData> FinalityCases() =>
        BeaconStateTestRunner.EnumerateCases("fulu", "finality", "finality");
}
