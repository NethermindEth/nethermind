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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Runner
{
    public class PerfTest : BlockchainTestBase, ITestInRunner
    {
        private readonly IBlockchainTestSource _testSource;

        public PerfTest(IBlockchainTestSource testSource) : base(testSource)
        {
            _testSource = testSource ?? throw new ArgumentNullException(nameof(testSource));
        }

        public async Task<CategoryResult> RunTests()
        {
            List<string> failingTests = new List<string>();
            long totalMs = 0L;
            Console.WriteLine($"RUNNING tests");
            Stopwatch stopwatch = new Stopwatch();
            IEnumerable<BlockchainTest> tests = _testSource.LoadTests();
            bool isNewLine = true;
            foreach (BlockchainTest test in tests)
            {
                stopwatch.Reset();
                Setup(null);
                try
                {
                    Assert.IsNull(test.LoadFailure);
                    await RunTest(test, stopwatch);
                }
                catch (Exception e)
                {
                    failingTests.Add(test.Name);
                    ConsoleColor mem = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    if (!isNewLine)
                    {
                        Console.WriteLine();
                        isNewLine = true;
                    }

                    Console.WriteLine($"  {test.Name,-80} {e.GetType().Name}");
                    Console.ForegroundColor = mem;
                }

                long ns = 1_000_000_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
                long ms = 1_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
                totalMs += ms;
                if (ms > 100)
                {
                    if (!isNewLine)
                    {
                        Console.WriteLine();
                        isNewLine = true;
                    }

                    Console.WriteLine($"  {test.Name,-80}{ns,14}ns{ms,8}ms");
                }
                else
                {
                    Console.Write(".");
                    isNewLine = false;
                }
            }

            if (!isNewLine)
            {
                Console.WriteLine();
            }

            return new CategoryResult(totalMs, failingTests.ToArray());
        }
    }
}