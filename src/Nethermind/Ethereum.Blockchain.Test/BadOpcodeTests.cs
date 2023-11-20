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
        // ToDo: investigate the reason, more likely test setup issue
        private static List<string> ignoredTests = new List<string>()
        {
            "badOpcodes_d25g0v0_",
            "undefinedOpcodeFirstByte_d0g0v0_"
        };

        [TestCaseSource(nameof(LoadTests))]
        [Retry(3)]
        public void Test(GeneralStateTest test)
        {
            Assert.True(RunTest(test).Pass);
        }

        public static IEnumerable<GeneralStateTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stBadOpcode");
            return ((IEnumerable<GeneralStateTest>)loader.LoadTests()).Where(x => !ignoredTests.Contains(x.Name));
        }
    }
}
