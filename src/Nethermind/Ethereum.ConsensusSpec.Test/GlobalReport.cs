// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Assembly-wide NUnit setup fixture: its OneTimeTearDown runs once, after every test in this assembly
/// has finished, regardless of fixture ordering - the one place the aggregate pass/fail/not-implemented
/// report in <see cref="ConsensusSpecTestSummary"/> can be printed exactly once with complete data.
/// </summary>
[SetUpFixture]
public class GlobalReport
{
    [OneTimeTearDown]
    public void PrintSummary() => ConsensusSpecTestSummary.PrintReport();
}
