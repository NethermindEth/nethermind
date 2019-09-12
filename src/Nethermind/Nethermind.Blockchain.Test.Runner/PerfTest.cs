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
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Runner
{
    public class PerfStateTest : BlockchainTestBase, IStateTestRunner
    {
        private readonly IBlockchainTestsSource _testsSource;

        public PerfStateTest(IBlockchainTestsSource testsSource)
        {
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
        }

        public IEnumerable<EthereumTestResult> RunTests()
        {
            List<EthereumTestResult> results = new List<EthereumTestResult>();
            Console.WriteLine($"RUNNING tests");
            Stopwatch stopwatch = new Stopwatch();
            IEnumerable<BlockchainTest> tests = _testsSource.LoadTests();
            bool isNewLine = true;
            foreach (BlockchainTest test in tests)
            {
                if (test.LoadFailure != null)
                {
                    continue;
                }
                
                Setup(NullLogManager.Instance);
                stopwatch.Restart();
                EthereumTestResult result = RunTest(test);
                stopwatch.Stop();
                results.Add(result);
                
                if (!result.Pass)
                {
                    ConsoleColor mem = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    if (!isNewLine)
                    {
                        Console.WriteLine();
                        isNewLine = true;
                    }

                    Console.WriteLine($"  {test.Name,-80} FAIL");
                    Console.ForegroundColor = mem;
                }

                long ns = 1_000_000_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
                long ms = 1_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
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

            return results;
        }
    }
}