// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Legacy.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class RefundTests : GeneralStateTestBase
    {
        [TestCaseSource(nameof(LoadTests))]
        public async Task Test(GeneralStateTest test) => (await RunTest(test)).Pass.Should().BeTrue();

        public static IEnumerable<GeneralStateTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(), "stRefundTest");
            return loader.LoadTests<GeneralStateTest>();
        }
    }
}

