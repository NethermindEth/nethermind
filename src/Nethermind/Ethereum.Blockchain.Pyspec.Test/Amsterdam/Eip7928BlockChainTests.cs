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
public class Eip7928BlockChainTests : BlockchainTestBase
{
    private const string Eip7928Wildcard = "eip7928_block_level_access_lists";

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => await RunTest(test);

    private static IEnumerable<BlockchainTest> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = Amsterdam.Constants.BalArchiveVersion,
            ArchiveName = Amsterdam.Constants.BalArchiveName
        }, "fixtures/blockchain_tests", Eip7928Wildcard);

        return loader.LoadTests().OfType<BlockchainTest>();
    }
}
