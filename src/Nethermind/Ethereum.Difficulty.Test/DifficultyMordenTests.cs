// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DifficultyMordenTests : TestsBase
    {
        public static IEnumerable<DifficultyTests> LoadMordenTests()
        {
            return LoadHex("difficultyMorden.json");
        }

        // ToDo: fix loader
        // [TestCaseSource(nameof(LoadMordenTests))]
        // public void Test(DifficultyTests test)
        // {
        //     RunTest(test, new MordenSpecProvider());
        // }
    }
}
