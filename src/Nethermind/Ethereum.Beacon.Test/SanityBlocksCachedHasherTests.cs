// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.StateTransition.Hashing;
using NUnit.Framework;

namespace Ethereum.Beacon.Test;

/// <summary>
/// Re-runs the consensus-spec <c>sanity/blocks</c> tests with the incremental
/// <see cref="CachedBeaconStateHasher"/> plugged into the transition, asserting the outcomes
/// match <see cref="SanityBlocksTests"/> (the post-state assertion still uses the full
/// merkleization, so any cached-root divergence fails the case).
/// </summary>
[TestFixture]
public class SanityBlocksCachedHasherTests
{
    [TestCaseSource(nameof(SanityBlocksCases))]
    public void Sanity_blocks_with_cached_hasher(string casePath) =>
        BeaconStateTestRunner.RunBlocksTest(casePath, new CachedBeaconStateHasher());

    private static IEnumerable<TestCaseData> SanityBlocksCases() =>
        BeaconStateTestRunner.EnumerateCases("fulu", "sanity", "blocks");
}
