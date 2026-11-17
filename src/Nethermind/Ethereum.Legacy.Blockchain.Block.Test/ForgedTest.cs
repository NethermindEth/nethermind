// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Legacy.Blockchain.Block.Test;

/// <summary>Runs <c>bcForgedTest</c>, which only the Constantinople set of the legacy tests has.</summary>
/// <remarks>
/// That set marks a block the client must reject only by leaving out its header, with no <c>expectException</c>, and
/// its last block hash names the block before. Such a block is read here as expecting an exception.
/// </remarks>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ForgedTest : BlockchainTestBase
{
    private const string RejectedWithoutHeader = "listed without a header, so it must be rejected";

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => await RunTest(test);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadLegacyBlockchainTestsStrategy(LegacyTestSet.Constantinople), "bcForgedTest")
            .LoadTests<BlockchainTest>()
            .Select(static test =>
            {
                foreach (TestBlockJson block in test.Blocks ?? [])
                {
                    if (block.BlockHeader is null)
                        block.ExpectException ??= RejectedWithoutHeader;
                }

                return test;
            });
}
