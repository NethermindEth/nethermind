// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core.Attributes;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class StateTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests)), Retry(3)]
        public async Task Test(BlockchainTest test)
        {
            if (test?.Name is not null && test.Name.Contains("SuicideStorage"))
            {
                Assert.Ignore("Covered by dedicated SuicideStorage tests");
            }

            await RunTest(test);
        }

        public static IEnumerable<BlockchainTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcStateTests");
            return loader.LoadTests<BlockchainTest>();
        }

        [TestCaseSource(nameof(LoadSuicideStorageTests)), Retry(3)]
        public async Task SuicideStorage_Tests(BlockchainTest test)
        {
            await RunTest(test);
        }

        public static IEnumerable<BlockchainTest> LoadSuicideStorageTests()
        {
            var loader = new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcStateTests", "SuicideStorage");
            return loader.LoadTests<BlockchainTest>();
        }
    }
}
