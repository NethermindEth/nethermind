// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Legacy.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class EIP3651WarmCoinbaseTests : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test)
    {
        Assert.That(RunTest(test).Pass, Is.True);
    }

    public static IEnumerable<GeneralStateTest> LoadTests()
    {
        const string testsSubset = "stEIP3651-" + "warm" + "coinbase";
        var loader = new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(), testsSubset);
        return loader.LoadTests<GeneralStateTest>();
    }
}
