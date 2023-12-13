// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System.Collections.Generic;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DifficultyByzantiumTests : TestsBase
    {
        public static IEnumerable<DifficultyTests> LoadFrontierTests()
        {
            return LoadHex("difficultyByzantium.json");
        }

        // ToDo: fix loader
        // [TestCaseSource(nameof(LoadFrontierTests))]
        // public void Test(DifficultyTests test)
        // {
        //     RunTest(test, new SingleReleaseSpecProvider(Byzantium.Instance, 1));
        // }    
    }
}
