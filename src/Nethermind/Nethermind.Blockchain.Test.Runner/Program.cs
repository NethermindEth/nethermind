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

namespace Nethermind.Blockchain.Test.Runner
{
    internal class Program
    {
        private const int StandardIterations = 1;

        private static readonly List<string> AllFailingTests = new List<string>();
        private static long _totalMs;

        private static async Task Run(ITestInRunner test, string category, string testWildcard, int iterations = StandardIterations)
        {
            CategoryResult result = await test.RunTests(category, testWildcard, iterations);

            AllFailingTests.AddRange(result.FailingTests);
            _totalMs += result.TotalMs;

            Console.WriteLine($"CATEGORY {category} {result.TotalMs}ms, FAILURES {result.FailingTests.Length}");
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine();
        }

        public static async Task Main(params string[] args)
        {
            while (true)
            {
                Console.WriteLine("P/B");
                string[] input = Console.ReadLine().Split();
                string command = input[0];
                string testWildcard = input.Length <= 1 ? null : input[1];

                Stopwatch stopwatch = new Stopwatch();
                if (command == "p")
                {
#if DEBUG
                    Console.WriteLine("Performance test should not run in the DEBUG mode");
#else
                    stopwatch.Start();
                    await Run(new PerfTest(), testWildcard);
#endif
                }
                else
                {
                    stopwatch.Start();
                    await Run(new BugHunter(), testWildcard);
                }
                stopwatch.Stop();
                
                foreach (string failingTest in AllFailingTests)
                {
                    ConsoleColor mem = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  FAILED: {failingTest}");
                    Console.ForegroundColor = mem;
                }

                Console.WriteLine($"FINISHED {_totalMs}ms, FAILURES {AllFailingTests.Count}");
                Console.WriteLine($"TOTAL TEST PROGRAM EXECUTION TIME: {stopwatch.Elapsed.TotalSeconds} SECONDS");
                Console.WriteLine("Press ENTER to continue");
                AllFailingTests.Clear();
                Console.ReadLine();
            }
        }

        private static async Task Run(ITestInRunner bugHunter, string testWildcard)
        {
            await Run(bugHunter, "stArgsZeroOneBalance", testWildcard);
            await Run(bugHunter, "stAttackTest", testWildcard);
            await Run(bugHunter, "stBadOpcode", testWildcard);
            await Run(bugHunter, "stBugs", testWildcard);
            await Run(bugHunter, "stCallCodes", testWildcard);
            await Run(bugHunter, "stCallCreateCallCodeTest", testWildcard);
            await Run(bugHunter, "stCallDelegateCodesCallCodeHomestead", testWildcard);
            await Run(bugHunter, "stCallDelegateCodesHomestead", testWildcard);
            await Run(bugHunter, "stChangedEIP150", testWildcard);
            await Run(bugHunter, "stCodeCopyTest", testWildcard);
            await Run(bugHunter, "stCodeSizeLimit", testWildcard);
            await Run(bugHunter, "stCreate2", testWildcard);
            await Run(bugHunter, "stCreateTest", testWildcard);
            await Run(bugHunter, "stDelegatecallTestHomestead", testWildcard);
            await Run(bugHunter, "stEIP150singleCodeGasPrices", testWildcard);
            await Run(bugHunter, "stEIP150Specific", testWildcard);
            await Run(bugHunter, "stEIP158Specific", testWildcard);
            await Run(bugHunter, "stExample", testWildcard);
            await Run(bugHunter, "stHomesteadSpecific", testWildcard);
            await Run(bugHunter, "stInitCodeTest", testWildcard);
            await Run(bugHunter, "stLogTests", testWildcard);
            await Run(bugHunter, "stMemExpandingEIP150Calls", testWildcard);
            await Run(bugHunter, "stMemoryStressTest", testWildcard);
            await Run(bugHunter, "stMemoryTest", testWildcard);
            await Run(bugHunter, "stNonZeroCallsTest", testWildcard);
            await Run(bugHunter, "stPreCompiledContracts", testWildcard);
            await Run(bugHunter, "stPreCompiledContracts2", testWildcard);
            await Run(bugHunter, "stQuadraticComplexityTest", testWildcard);
            await Run(bugHunter, "stRandom", testWildcard);
            await Run(bugHunter, "stRandom2", testWildcard);
            await Run(bugHunter, "stRecursiveCreate", testWildcard);
            await Run(bugHunter, "stRefundTest", testWildcard);
            await Run(bugHunter, "stReturnDataTest", testWildcard);
            await Run(bugHunter, "stRevertTest", testWildcard);
            await Run(bugHunter, "stShift", testWildcard);
            await Run(bugHunter, "stSolidityTest", testWildcard);
            await Run(bugHunter, "stSpecialTest", testWildcard);
            await Run(bugHunter, "stStackTests", testWildcard);
            await Run(bugHunter, "stStaticCall", testWildcard);
            await Run(bugHunter, "stSystemOperationsTest", testWildcard);
            await Run(bugHunter, "stTransactionTest", testWildcard);
            await Run(bugHunter, "stTransitionTest", testWildcard);
            await Run(bugHunter, "stWalletTest", testWildcard);
            await Run(bugHunter, "stZeroCallsRevert", testWildcard);
            await Run(bugHunter, "stZeroCallsTest", testWildcard);
            await Run(bugHunter, "stZeroKnowledge", testWildcard);
            await Run(bugHunter, "stZeroKnowledge2", testWildcard);
            await Run(bugHunter, "bcBlockGasLimitTest", testWildcard);
            await Run(bugHunter, "bcExploitTest", testWildcard);
            await Run(bugHunter, "bcForgedTest", testWildcard);
            await Run(bugHunter, "bcForkStressTest", testWildcard);
            await Run(bugHunter, "bcGasPricerTest", testWildcard);
            await Run(bugHunter, "bcInvalidHeaderTest", testWildcard);
            await Run(bugHunter, "bcMultiChainTestTest", testWildcard);
            await Run(bugHunter, "bcRandomBlockhashTest", testWildcard);
            await Run(bugHunter, "bcStateTests", testWildcard);
            await Run(bugHunter, "bcTotalDifficultyTest", testWildcard);
            await Run(bugHunter, "bcUncleHeaderValidity", testWildcard);
            await Run(bugHunter, "bcUncleTest", testWildcard);
            await Run(bugHunter, "bcValidBlockTest", testWildcard);
            await Run(bugHunter, "bcWalletTest", testWildcard);

            /* transition tests */
            await Run(bugHunter, "bcEIP158ToByzantium", testWildcard);
            await Run(bugHunter, "bcFrontierToHomestead", testWildcard);
            await Run(bugHunter, "bcHomesteadToDao", testWildcard);
            await Run(bugHunter, "bcHomesteadToEIP150", testWildcard);
        }
    }
}