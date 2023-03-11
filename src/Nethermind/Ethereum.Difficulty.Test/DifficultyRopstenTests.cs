// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DifficultyRopstenTests : TestsBase
    {
        public static IEnumerable<DifficultyTests> LoadRopstenTests()
        {
            return LoadHex("difficultyRopsten.json");
        }

        [TestCaseSource(nameof(LoadRopstenTests))]
        public void Test(DifficultyTests test)
        {
            RunTest(test, RopstenSpecProvider.Instance);
        }
    }
}
