// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class Eip8024StateTests : GeneralStateTestBase
{
    private const string ArchiveVersion = "bal@v5.2.0";
    private const string ArchiveName = "fixtures_bal.tar.gz";
    private const string Eip8024Wildcard = "eip8024_dupn_swapn_exchange";

    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test) => RunTest(test).Pass.Should().BeTrue();

    private static IEnumerable<GeneralStateTest> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = ArchiveVersion,
            ArchiveName = ArchiveName
        }, "fixtures/state_tests", Eip8024Wildcard);

        return loader.LoadTests<GeneralStateTest>();
    }
}
