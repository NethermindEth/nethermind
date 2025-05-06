// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class WalletTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests)), Retry(3)]
        public async Task Test(BlockchainTest test)
        {
            await RunTest(test);
        }
        public static IEnumerable<BlockchainTest> LoadTests()
        {
            var loader = new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcWalletTest");
            return loader.LoadTests<BlockchainTest>();
        }
    }
}
