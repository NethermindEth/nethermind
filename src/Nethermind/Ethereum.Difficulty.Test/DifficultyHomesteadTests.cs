// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DifficultyHomesteadTests : TestsBase
    {
        public static IEnumerable<DifficultyTests> LoadHomesteadTests()
        {
            return LoadHex("difficultyHomestead.json");
        }

        // ToDo: fix loader
        // [TestCaseSource(nameof(LoadHomesteadTests))]
        // public void Test(DifficultyTests test)
        // {
        //     RunTest(test, new SingleReleaseSpecProvider(Homestead.Instance, 1));
        // }
    }
}
