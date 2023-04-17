// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Transition.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class MergeToShanghaiTests : BlockchainTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public Task Test(BlockchainTest test)
    {
        // ToDo Starting from the Shanghai the transition tests are not longer working on blockNumber, but timestamp, so this test needs to be fixed - Test Setup Bug
        // await RunTest(test);
        return Task.CompletedTask;
    }

    public static IEnumerable<BlockchainTest> LoadTests()
    {
        var loader = new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcMergeToShanghai");
        return (IEnumerable<BlockchainTest>)loader.LoadTests();
    }
}
