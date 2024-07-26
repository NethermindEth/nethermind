// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DifficultyFrontierTests : TestsBase
    {
        public static IEnumerable<DifficultyTests> LoadFrontierTests()
        {
            return LoadHex("difficultyFrontier.json");
        }

        // ToDo: fix loader
        // [TestCaseSource(nameof(LoadFrontierTests))]
        // public void Test(DifficultyTests test)
        // {
        //     RunTest(test, new SingleReleaseSpecProvider(Frontier.Instance, 1));
        // }    
    }
}
