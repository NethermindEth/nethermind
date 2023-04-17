// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Legacy.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class TransitionTests : GeneralStateTestBase
    {
        [TestCaseSource(nameof(LoadTests))]
        public void Test(GeneralStateTest test)
        {
            Assert.True(RunTest(test).Pass);
        }

        public static IEnumerable<GeneralStateTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(), "stTransitionTest");
            return (IEnumerable<GeneralStateTest>)loader.LoadTests();
        }
    }
}

