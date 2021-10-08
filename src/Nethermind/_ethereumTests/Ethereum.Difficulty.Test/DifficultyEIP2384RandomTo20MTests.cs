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
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DifficultyEIP2384RandomTo20MTests : TestsBase
    {     
        public static IEnumerable<DifficultyTests> LoadEIP2384Tests()
        {
            return LoadHex("difficultyEIP2384_random_to20M.json");
        }

        [TestCaseSource(nameof(LoadEIP2384Tests))]
        public void Test(DifficultyTests test)
        {
            RunTest(test, new SingleReleaseSpecProvider(MuirGlacier.Instance, 1));
        }
    }
}
