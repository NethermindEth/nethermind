// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class CancunTests : BlockchainTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => await RunTest(test);

    private static IEnumerable<BlockchainTest> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadPyspecTestsStrategy(), $",,fixtures/cancun");
        return (IEnumerable<BlockchainTest>)loader.LoadTests();
    }
}
