// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class EOFTests : GeneralStateTestBase
{
    // Uncomment when EOF tests are merged

    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test)
    {
        Assert.True(RunTest(test).Pass);
    }

    public static IEnumerable<GeneralStateTest> LoadTests()
    {
        var eip3540Loader = (IEnumerable<GeneralStateTest>)(new TestsSourceLoader(new LoadEofTestsStrategy(), "stEIP3540").LoadTests());
        var eip3670Loader = (IEnumerable<GeneralStateTest>)(new TestsSourceLoader(new LoadEofTestsStrategy(), "stEIP3670").LoadTests());
        var eip4200Loader = (IEnumerable<GeneralStateTest>)(new TestsSourceLoader(new LoadEofTestsStrategy(), "stEIP4200").LoadTests());
        var eip4750Loader = (IEnumerable<GeneralStateTest>)(new TestsSourceLoader(new LoadEofTestsStrategy(), "stEIP4750").LoadTests());
        var eip5450Loader = (IEnumerable<GeneralStateTest>)(new TestsSourceLoader(new LoadEofTestsStrategy(), "stEIP5450").LoadTests());
        return eip3540Loader.Concat(eip3670Loader).Concat(eip4200Loader).Concat(eip4750Loader).Concat(eip5450Loader);
    }
}
