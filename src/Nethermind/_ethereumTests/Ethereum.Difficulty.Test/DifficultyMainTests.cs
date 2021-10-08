/*
 * Copyright (c) 2021 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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
