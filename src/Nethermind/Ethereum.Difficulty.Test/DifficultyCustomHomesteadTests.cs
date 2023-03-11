// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DifficultyCustomHomesteadTests : TestsBase
    {
        public static IEnumerable<DifficultyTests> LoadFrontierTests()
        {
            return LoadHex("difficultyCustomHomestead.json");
        }

        [TestCaseSource(nameof(LoadFrontierTests))]
        public void Test(DifficultyTests test)
        {
            RunTest(test, new TestSingleReleaseSpecProvider(Homestead.Instance));
        }
    }
}
