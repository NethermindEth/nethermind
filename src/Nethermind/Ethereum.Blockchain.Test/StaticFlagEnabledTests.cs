// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class StaticFlagEnabledTests : GeneralStateTestBase
    {
        [TestCaseSource(nameof(LoadTests))]
        public async Task Test(GeneralStateTest test) => (await RunTest(test)).Pass.Should().BeTrue();

        public static IEnumerable<GeneralStateTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stStaticFlagEnabled");
            return loader.LoadTests<GeneralStateTest>();
        }
    }
}
