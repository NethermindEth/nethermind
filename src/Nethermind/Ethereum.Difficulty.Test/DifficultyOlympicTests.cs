// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DifficultyOlympicTests : TestsBase
    {
        public static IEnumerable<DifficultyTests> LoadOlympicTests()
        {
            return LoadHex("difficultyOlympic.json");
        }

        // ToDo: fix loader
        // [TestCaseSource(nameof(LoadOlympicTests))]
        // public void Test(DifficultyTests test)
        // {
        //     RunTest(test, new SingleReleaseSpecProvider(Olympic.Instance, 0));
        // }
    }
}
