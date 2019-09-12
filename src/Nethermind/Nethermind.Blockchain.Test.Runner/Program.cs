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

namespace Nethermind.Blockchain.Test.Runner
{
    internal class Program
    {
        private static readonly List<string> AllFailingTests = new List<string>();
        private static long _totalMs;

        public static async Task Main(params string[] args)
        {
            await Run();
        }

        private static async Task Run()
        {
            await RunManualTestingLoop();
        }

        private static async Task RunManualTestingLoop()
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
                    await RunAllStateTests(testWildcard, s => new PerfTest(s));
#endif
                }
                else if(command == "b")
                {
                    stopwatch.Start();
                    await RunAllStateTests(testWildcard, s => new BugHunter(s));
                }
                else
                {
                    break;
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

        private static async Task WrapAndRunDirectoryTests(IStateTestRunner stateTest)
        {
            var result = (await stateTest.RunTests()).ToList();
            var failedTestsInCategory = result.Where(r => !r.Pass).Select(t => t.Name + " " + t.LoadFailure).ToArray();
            AllFailingTests.AddRange(failedTestsInCategory);
            long categoryTimeInMs = result.Sum(t => t.TimeInMs);
            _totalMs += result.Sum(t => t.TimeInMs);

            Console.WriteLine($"CATEGORY {categoryTimeInMs}ms, FAILURES {failedTestsInCategory.Length}");
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine();
        }

        private static async Task RunAllStateTests(string testWildcard, Func<IBlockchainTestsSource, IStateTestRunner> testRunnerBuilder)
        {
            // do not loop over a list of directories so it is more convenient to rapidly comment out / comment in
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stArgsZeroOneBalance", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stAttackTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stBadOpcode", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stBugs", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCallCodes", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCallCreateCallCodeTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCallDelegateCodesCallCodeHomestead", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCallDelegateCodesHomestead", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stChangedEIP150", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCodeCopyTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCodeSizeLimit", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCreate2", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCreateTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stDelegatecallTestHomestead", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stEIP150singleCodeGasPrices", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stEIP150Specific", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stEIP158Specific", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stExample", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stHomesteadSpecific", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stInitCodeTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stLogTests", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stMemExpandingEIP150Calls", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stMemoryStressTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stMemoryTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stNonZeroCallsTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stPreCompiledContracts", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stPreCompiledContracts2", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stQuadraticComplexityTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stRandom", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stRandom2", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stRecursiveCreate", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stRefundTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stReturnDataTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stRevertTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stShift", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stSolidityTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stSpecialTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stStackTests", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stStaticCall", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stSystemOperationsTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stTransactionTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stTransitionTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stWalletTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stZeroCallsRevert", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stZeroCallsTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stZeroKnowledge", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stZeroKnowledge2", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcBlockGasLimitTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcExploitTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcForgedTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcForkStressTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcGasPricerTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcInvalidHeaderTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcMultiChainTestTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcRandomBlockhashTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcStateTests", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcTotalDifficultyTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcUncleHeaderValidity", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcUncleTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcValidBlockTest", testWildcard)));
            await WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcWalletTest", testWildcard)));

            /* 
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcEIP158ToByzantium", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcFrontierToHomestead", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcHomesteadToDao", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcHomesteadToEIP150", testWildcard));
            */
        }
    }
}