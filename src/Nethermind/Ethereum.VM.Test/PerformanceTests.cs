// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.VM.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    // ReSharper disable once InconsistentNaming
    public class PerformanceTests : GeneralStateTestBase
    {
        [TestCaseSource(nameof(LoadTests))]
        [Retry(3)]
        public async Task Test(GeneralStateTest test) => (await RunTest(test)).Pass.Should().BeTrue();

        public static IEnumerable<GeneralStateTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "vmPerformance");
            return loader.LoadTests<GeneralStateTest>();
        }
    }
}
