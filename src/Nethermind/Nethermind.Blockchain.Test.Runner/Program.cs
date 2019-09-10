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

namespace Nethermind.Blockchain.Test.Runner
{
    internal class Program
    {
        private static readonly List<string> AllFailingTests = new List<string>();
        private static long _totalMs;

        private static async Task Run(ITestInRunner test)
        {
            CategoryResult result = await test.RunTests();

            AllFailingTests.AddRange(result.FailingTests);
            _totalMs += result.TotalMs;

            Console.WriteLine($"CATEGORY {result.TotalMs}ms, FAILURES {result.FailingTests.Length}");
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
                    await Run(testWildcard, s => new PerfTest(s));
#endif
                }
                else
                {
                    stopwatch.Start();
                    await Run(testWildcard, s => new BugHunter(s));
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

        private static async Task Run(string testWildcard, Func<IBlockchainTestsSource, ITestInRunner> testRunnerBuilder)
        {
            await Run(testRunnerBuilder(new DirectoryTestsSource("stArgsZeroOneBalance", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stAttackTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stBadOpcode", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stBugs", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stCallCodes", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stCallCreateCallCodeTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stCallDelegateCodesCallCodeHomestead", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stCallDelegateCodesHomestead", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stChangedEIP150", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stCodeCopyTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stCodeSizeLimit", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stCreate2", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stCreateTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stDelegatecallTestHomestead", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stEIP150singleCodeGasPrices", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stEIP150Specific", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stEIP158Specific", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stExample", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stHomesteadSpecific", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stInitCodeTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stLogTests", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stMemExpandingEIP150Calls", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stMemoryStressTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stMemoryTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stNonZeroCallsTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stPreCompiledContracts", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stPreCompiledContracts2", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stQuadraticComplexityTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stRandom", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stRandom2", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stRecursiveCreate", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stRefundTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stReturnDataTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stRevertTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stShift", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stSolidityTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stSpecialTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stStackTests", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stStaticCall", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stSystemOperationsTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stTransactionTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stTransitionTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stWalletTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stZeroCallsRevert", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stZeroCallsTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stZeroKnowledge", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("stZeroKnowledge2", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcBlockGasLimitTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcExploitTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcForgedTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcForkStressTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcGasPricerTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcInvalidHeaderTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcMultiChainTestTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcRandomBlockhashTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcStateTests", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcTotalDifficultyTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcUncleHeaderValidity", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcUncleTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcValidBlockTest", testWildcard)));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcWalletTest", testWildcard)));

            /* transitnew BugHunter(new FileBlockhainTestSource()/
            await Run(new BugHunter(new FileBlockhainTestSource("bcEIP158ToByzantium", testWildcard));
            await Run(new BugHunter(new FileBlockhainTestSource("bcFrontierToHomestead", testWildcard));
            await Run(new BugHunter(new FileBlockhainTestSource("bcHomesteadToDao", testWildcard));
            await Run(new BugHunter(new FileBlockhainTestSource("bcHomesteadToEIP150", testWildcard));
            */
        }
    }
}