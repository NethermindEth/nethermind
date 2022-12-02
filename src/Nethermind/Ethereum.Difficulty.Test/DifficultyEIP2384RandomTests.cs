// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DifficultyEIP2384RandomTests : TestsBase
    {
        public static IEnumerable<DifficultyTests> LoadEIP2384Tests()
        {
            return LoadHex("difficultyEIP2384_random.json");
        }

        // ToDo: fix loader
        // [TestCaseSource(nameof(LoadEIP2384Tests))]
        // public void Test(DifficultyTests test)
        // {
        //     RunTest(test, new SingleReleaseSpecProvider(MuirGlacier.Instance, 1));
        // }
    }
}
