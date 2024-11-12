// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Legacy.Test;

[Parallelizable(ParallelScope.All)]
public class ArgsZeroOneBalanaceTests : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test)
    {
        Assert.That(RunTest(test).Pass, Is.True);
    }

    public static IEnumerable<GeneralStateTest> LoadTests()
    {
        var loader = new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(), "stArgsZeroOneBalance");
        return (IEnumerable<GeneralStateTest>)loader.LoadTests();
    }
}
