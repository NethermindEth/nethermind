// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class Eip3540Tests : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test)
    {
        Assert.True(RunTest(test).Pass);
    }

    public static IEnumerable<GeneralStateTest> LoadTests()
    {
        var loader = new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stEIP3540");
        return (IEnumerable<GeneralStateTest>)loader.LoadTests();
    }
}
