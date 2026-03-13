// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class Eip7843StateTests : GeneralStateTestBase
{
    private const string Eip7843Wildcard = "eip7843_slotnum";

    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test) => RunTest(test).Pass.Should().BeTrue();

    private static IEnumerable<GeneralStateTest> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = Amsterdam.Constants.BalArchiveVersion,
            ArchiveName = Amsterdam.Constants.BalArchiveName
        }, "fixtures/state_tests", Eip7843Wildcard);

        return loader.LoadTests<GeneralStateTest>();
    }
}
