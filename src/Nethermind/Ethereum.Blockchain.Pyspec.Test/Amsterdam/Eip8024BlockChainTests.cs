// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class Eip8024BlockChainTests : BlockchainTestBase
{
    private const string ArchiveVersion = "bal@v5.2.0";
    private const string ArchiveName = "fixtures_bal.tar.gz";
    private const string Eip8024Wildcard = "eip8024_dupn_swapn_exchange";

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => await RunTest(test);

    private static IEnumerable<BlockchainTest> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = ArchiveVersion,
            ArchiveName = ArchiveName
        }, "fixtures/blockchain_tests", Eip8024Wildcard);

        return loader.LoadTests().OfType<BlockchainTest>();
    }
}
