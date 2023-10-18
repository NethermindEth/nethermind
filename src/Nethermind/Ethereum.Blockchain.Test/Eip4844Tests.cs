// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class EIP4844blobtransactionsTests : GeneralStateTestBase
{
    // Uncomment when stEIP4844 tests are merged

    // [TestCaseSource(nameof(LoadTests))]
    // public void Test(GeneralStateTest test)
    // {
    //     Assert.True(RunTest(test).Pass);
    // }

    public static IEnumerable<GeneralStateTest> LoadTests()
    {
        var loader = new TestsSourceLoader(new LoadEipTestsStrategy(), "stEIP4844-blobtransactions");
        return (IEnumerable<GeneralStateTest>)loader.LoadTests();
    }
}
