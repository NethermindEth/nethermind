// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EthStats.Integrations;
using NUnit.Framework;

namespace Nethermind.EthStats.Test;

public class EthStatsIntegrationTests
{
    [TestCase(3, 1, true, 1, 3, TestName = "TryNormalizeHistoryRange_swaps_min_and_max")]
    [TestCase(-3, -1, false, 0, 0, TestName = "TryNormalizeHistoryRange_rejects_negative_range")]
    [TestCase(-3, 3, true, 0, 3, TestName = "TryNormalizeHistoryRange_clamps_negative_min")]
    [TestCase(0, 100, true, 37, 100, TestName = "TryNormalizeHistoryRange_limits_oversized_range")]
    public void TryNormalizeHistoryRange_handles_edges(
        long requestMin,
        long requestMax,
        bool expectedResult,
        long expectedMin,
        long expectedMax)
    {
        bool result = EthStatsIntegration.TryNormalizeHistoryRange(
            new EthStatsHistoryRequest(requestMin, requestMax),
            out long min,
            out long max);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expectedResult));
            Assert.That(min, Is.EqualTo(expectedMin));
            Assert.That(max, Is.EqualTo(expectedMax));
        }
    }
}
