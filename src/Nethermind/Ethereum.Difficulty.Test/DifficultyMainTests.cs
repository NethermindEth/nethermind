// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DifficultyMainNetworkTests : TestsBase
    {
        public static IEnumerable<DifficultyTests> LoadBasicTests()
        {
            return Load("difficulty.json");
        }

        public static IEnumerable<DifficultyTests> LoadMainNetworkTests()
        {
            return LoadHex("difficultyMainNetwork.json");
        }

        [TestCaseSource(nameof(LoadBasicTests))]
        public void Test_basic(DifficultyTests test)
        {
            RunTest(test, MainnetSpecProvider.Instance);
        }

        [TestCaseSource(nameof(LoadMainNetworkTests))]
        public void Test_main(DifficultyTests test)
        {
            RunTest(test, MainnetSpecProvider.Instance);
        }
    }
}
