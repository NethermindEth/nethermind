// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Legacy.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class SStoreTests : GeneralStateTestBase
    {
        [TestCaseSource(nameof(LoadTests))]
        public void Test(GeneralStateTest test)
        {
            Assert.True(RunTest(test).Pass);
        }

        public static IEnumerable<GeneralStateTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(), "stSStoreTest");
            return (IEnumerable<GeneralStateTest>)loader.LoadTests();
        }
    }
}

