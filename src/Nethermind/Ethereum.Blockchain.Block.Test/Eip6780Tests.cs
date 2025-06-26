// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class Eip6780Tests : BlockchainTestBase
{
    //[TestCaseSource(nameof(LoadTests))]
    //public async Task Test(BlockchainTest test)
    //{
    //    await RunTest(test);
    //}

    public static IEnumerable<BlockchainTest> LoadTests()
    {
        var loader = new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcEIP6780");
        return loader.LoadTests<BlockchainTest>();
    }
}
