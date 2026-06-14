// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using NUnit.Framework;

namespace Ethereum.Beacon.Test;

/// <summary>
/// Consensus-spec <c>sanity/blocks</c> tests: full signed-block transitions through
/// <c>StateTransition.Apply</c>, including invalid-block rejection cases.
/// </summary>
[TestFixture]
public class SanityBlocksTests
{
    [TestCaseSource(nameof(SanityBlocksCases))]
    public void Sanity_blocks(string casePath) => BeaconStateTestRunner.RunBlocksTest(casePath);

    private static IEnumerable<TestCaseData> SanityBlocksCases() =>
        BeaconStateTestRunner.EnumerateCases("fulu", "sanity", "blocks");
}
