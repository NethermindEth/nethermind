// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class ZeroCallsRevertTests : GeneralStateTestBase
    {
        [TestCaseSource(nameof(LoadTests))]
        public void Test(GeneralStateTest test)
        {
            Assert.That(RunTest(test).Pass, Is.True);
        }

        public static IEnumerable<GeneralStateTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stZeroCallsRevert");
            return loader.LoadTests<GeneralStateTest>();
        }
    }
}
