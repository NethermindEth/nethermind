// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class Eip5656Tests : GeneralStateTestBase
{
    // wait untill Eip5656 tests are merged to de-comment this

    // [TestCaseSource(nameof(LoadTests))]
    // public void Test(GeneralStateTest test)
    // {
    //     Assert.True(RunTest(test).Pass);
    // }

    public static IEnumerable<GeneralStateTest> LoadTests()
    {
        var loader = new TestsSourceLoader(new LoadEipTestsStrategy(), "stEIP5656-MCOPY");
        return (IEnumerable<GeneralStateTest>)loader.LoadTests();
    }
}
