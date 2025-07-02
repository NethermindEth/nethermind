// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Legacy.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class RevertTests : GeneralStateTestBase
    {
        [TestCaseSource(nameof(LoadTests))]
        public void Test(GeneralStateTest test)
        {
            Assert.That(RunTest(test).Pass, Is.True);
        }

        public static IEnumerable<GeneralStateTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(), "stRevertTest");
            IEnumerable<GeneralStateTest> tests = loader.LoadTests<GeneralStateTest>();
            HashSet<string> ignoredTests = new()
            {
                "RevertPrecompiledTouch",
            };

            return tests.Where(t => !ignoredTests.Any(pattern => t.Name.Contains(pattern))); ;
        }
    }
}

