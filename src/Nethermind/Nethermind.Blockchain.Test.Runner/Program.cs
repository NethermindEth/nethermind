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

        private static async Task Run(string testWildcard, Func<IBlockchainTestSource, ITestInRunner> testRunnerBuilder)
        {
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stArgsZeroOneBalance", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stAttackTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stBadOpcode", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stBugs", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stCallCodes", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stCallCreateCallCodeTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stCallDelegateCodesCallCodeHomestead", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stCallDelegateCodesHomestead", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stChangedEIP150", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stCodeCopyTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stCodeSizeLimit", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stCreate2", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stCreateTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stDelegatecallTestHomestead", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stEIP150singleCodeGasPrices", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stEIP150Specific", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stEIP158Specific", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stExample", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stHomesteadSpecific", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stInitCodeTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stLogTests", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stMemExpandingEIP150Calls", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stMemoryStressTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stMemoryTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stNonZeroCallsTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stPreCompiledContracts", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stPreCompiledContracts2", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stQuadraticComplexityTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stRandom", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stRandom2", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stRecursiveCreate", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stRefundTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stReturnDataTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stRevertTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stShift", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stSolidityTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stSpecialTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stStackTests", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stStaticCall", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stSystemOperationsTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stTransactionTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stTransitionTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stWalletTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stZeroCallsRevert", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stZeroCallsTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stZeroKnowledge", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("stZeroKnowledge2", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcBlockGasLimitTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcExploitTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcForgedTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcForkStressTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcGasPricerTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcInvalidHeaderTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcMultiChainTestTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcRandomBlockhashTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcStateTests", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcTotalDifficultyTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcUncleHeaderValidity", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcUncleTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcValidBlockTest", testWildcard)));
            await Run(testRunnerBuilder(new FileBlockchainTestSource("bcWalletTest", testWildcard)));

            /* transitnew BugHunter(new FileBlockhainTestSource()/
            await Run(new BugHunter(new FileBlockhainTestSource("bcEIP158ToByzantium", testWildcard));
            await Run(new BugHunter(new FileBlockhainTestSource("bcFrontierToHomestead", testWildcard));
            await Run(new BugHunter(new FileBlockhainTestSource("bcHomesteadToDao", testWildcard));
            await Run(new BugHunter(new FileBlockhainTestSource("bcHomesteadToEIP150", testWildcard));
            */
        }
    }
}