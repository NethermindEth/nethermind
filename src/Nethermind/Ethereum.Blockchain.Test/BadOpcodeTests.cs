// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class BadOpcodeTests : GeneralStateTestBase
    {
        [TestCaseSource(nameof(LoadTests))]
        [Retry(3)]
        public void Test(GeneralStateTest test)
        {
            Assert.True(RunTest(test).Pass);
        }

        public static IEnumerable<GeneralStateTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stBadOpcode");
            return (IEnumerable<GeneralStateTest>)loader.LoadTests();
        }
    }
}
