// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Legacy.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class RevertTest : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(GeneralStateTest test)
    {
        EthereumTestResult result = await RunTest(test);
        Assert.That(result.Pass, Is.True, result.Error);
    }

    public static IEnumerable<GeneralStateTest> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadLegacyGeneralStateTestsStrategy(), "stRevertTest");
        IEnumerable<GeneralStateTest> tests = loader.LoadTests<GeneralStateTest>();
        HashSet<string> ignoredTests = ["RevertPrecompiledTouch"];
        return tests.Where(t => !ignoredTests.Any(pattern => t.Name.Contains(pattern)));
    }
}
