// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

[TestFixture]
[Ignore("EOF")]
[Parallelizable(ParallelScope.All)]
public class OsakaDevnet2BlockChainTests : BlockchainTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => await RunTest(test);

    private static IEnumerable<TestCaseData> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadPyspecTestsStrategy()
        {
            ArchiveName = "fixtures_fusaka-devnet-2.tar.gz",
            ArchiveVersion = "fusaka-devnet-2@v1.2.0"
        }, $"fixtures/blockchain_tests/osaka");
        return loader.LoadTests().OfType<BlockchainTest>().Select(t => new TestCaseData(t)
            .SetName(t.Name)
            .SetCategory(t.Category));
    }
}
