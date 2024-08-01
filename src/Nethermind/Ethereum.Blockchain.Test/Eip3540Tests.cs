// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class Eip3540Tests : GeneralStateTestBase
{
    // ToDo: Eip3540 is in development phase on another branch. This will be uncommented after merging that branch.

    // [TestCaseSource(nameof(LoadTests))]
    // public void Test(GeneralStateTest test)
    // {
    //     Assert.True(RunTest(test).Pass);
    // }

    public static IEnumerable<GeneralStateTest> LoadTests()
    {
        var loader = new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stEIP3540");
        return (IEnumerable<GeneralStateTest>)loader.LoadTests();
    }
}
