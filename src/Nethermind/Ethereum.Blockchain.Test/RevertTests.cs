/*
 * Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using Ethereum.Test.Base;
using Nethermind.Core;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture][Parallelizable(ParallelScope.All)]
    public class RevertTests : BlockchainTestBase
    {
        private string[] ignored = new string[]
        {
            "RevertPrecompiledTouch_d0g0v0",
            "RevertPrecompiledTouch_d3g0v0",
            "RevertPrecompiledTouchExactOOG_d7g1v0",
            "RevertPrecompiledTouchExactOOG_d7g2v0",
            "RevertPrecompiledTouchExactOOG_d31g1v0",
            "RevertPrecompiledTouchExactOOG_d31g2v0",
            "RevertPrecompiledTouch_storage_d0g0v0",
            "RevertPrecompiledTouch_storage_d3g0v0",
            "TouchToEmptyAccountRevert3_d0g0v0"
        };
        
        [Todo(Improve.TestCoverage, "Investigate if the skipped tests only affected by retesteth - they worked before the test format changes")]
        [TestCaseSource(nameof(LoadTests))]
        public void Test(BlockchainTest test)
        {
            if (ignored.Any(i => test.Name.Contains(i)))
            {
                return;
            }
            
            Assert.True(RunTest(test).Pass);
        }
        
        public static IEnumerable<BlockchainTest> LoadTests() { return new DirectoryTestsSource("stRevertTest").LoadTests(); }
    }
}