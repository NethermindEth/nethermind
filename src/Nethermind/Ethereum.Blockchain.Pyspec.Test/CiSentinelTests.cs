// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

// Microsoft Testing Platform exits non-zero when zero tests run. On runners
// where every Pyspec test is filtered out by CiRunnerGuard (non-Linux-x64 CI,
// other shards), that turns a fully-skipped job into a failure. This fixture
// provides a single always-running test so the runner has at least one
// non-skipped result to report.
[TestFixture]
public class CiSentinelTests
{
    [Test]
    public void AlwaysPasses() => Assert.Pass();
}
