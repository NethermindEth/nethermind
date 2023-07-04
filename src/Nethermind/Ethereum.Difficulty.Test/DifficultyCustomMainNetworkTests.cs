// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DifficultyCustomMainNetworkTests : TestsBase
    {
        public static IEnumerable<DifficultyTests> LoadFrontierTests()
        {
            return LoadHex("difficultyCustomMainNetwork.json");
        }

        [TestCaseSource(nameof(LoadFrontierTests))]
        public void Test(DifficultyTests test)
        {
            RunTest(test, MainnetSpecProvider.Instance);
        }
    }
}
