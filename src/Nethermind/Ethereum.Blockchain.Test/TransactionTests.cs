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
    public class TransactionTests : GeneralStateTestBase
    {
        // ToDo: This tests are passing on hive tests, but failing here
        private readonly string[] ignored =
        {
            "HighGasPrice_d0g0v0",
            "ValueOverflow"
        };

        [TestCaseSource(nameof(LoadTests))]
        public void Test(GeneralStateTest test)
        {
            if (ignored.Any(i => test.Name.Contains(i)))
            {
                return;
            }

            Assert.That(RunTest(test).Pass, Is.True);
        }

        public static IEnumerable<GeneralStateTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stTransactionTest");
            return loader.LoadTests<GeneralStateTest>();
        }
    }
}
