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
using Ethereum.Test.Base;

namespace Nethermind.Blockchain.Test.Runner
{
    internal class Program
    {
        private static readonly List<string> AllFailingTests = new List<string>();
        private static long _totalMs;

        public static void Main(params string[] args)
        {
            Run();
        }

        private static void Run()
        {
            RunManualTestingLoop();
        }

        private static void RunManualTestingLoop()
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
                    RunAllStateTests(testWildcard, s => new PerfStateTest(s));
#endif
                }
                else if(command == "b")
                {
                    stopwatch.Start();
                    RunAllStateTests(testWildcard, s => new BugHunter(s));
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

        private static void WrapAndRunDirectoryTests(IStateTestRunner stateTest)
        {
            var result = stateTest.RunTests().ToList();
            var failedTestsInCategory = result.Where(r => !r.Pass).Select(t => t.Name + " " + t.LoadFailure).ToArray();
            AllFailingTests.AddRange(failedTestsInCategory);
            long categoryTimeInMs = result.Sum(t => t.TimeInMs);
            _totalMs += result.Sum(t => t.TimeInMs);

            Console.WriteLine($"CATEGORY {categoryTimeInMs}ms, FAILURES {failedTestsInCategory.Length}");
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine();
        }

        private static void RunAllStateTests(string testWildcard, Func<IBlockchainTestsSource, IStateTestRunner> testRunnerBuilder)
        {
            // do not loop over a list of directories so it is more convenient to rapidly comment out / comment in
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stArgsZeroOneBalance", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stAttackTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stBadOpcode", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stBugs", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCallCodes", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCallCreateCallCodeTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCallDelegateCodesCallCodeHomestead", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCallDelegateCodesHomestead", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stChangedEIP150", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCodeCopyTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCodeSizeLimit", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCreate2", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stCreateTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stDelegatecallTestHomestead", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stEIP150singleCodeGasPrices", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stEIP150Specific", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stEIP158Specific", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stExample", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stHomesteadSpecific", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stInitCodeTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stLogTests", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stMemExpandingEIP150Calls", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stMemoryStressTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stMemoryTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stNonZeroCallsTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stPreCompiledContracts", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stPreCompiledContracts2", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stQuadraticComplexityTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stRandom", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stRandom2", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stRecursiveCreate", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stRefundTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stReturnDataTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stRevertTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stShift", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stSolidityTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stSpecialTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stStackTests", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stStaticCall", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stSystemOperationsTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stTransactionTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stTransitionTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stWalletTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stZeroCallsRevert", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stZeroCallsTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stZeroKnowledge", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("stZeroKnowledge2", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcBlockGasLimitTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcExploitTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcForgedTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcForkStressTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcGasPricerTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcInvalidHeaderTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcMultiChainTestTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcRandomBlockhashTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcStateTests", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcTotalDifficultyTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcUncleHeaderValidity", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcUncleTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcValidBlockTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new DirectoryTestsSource("bcWalletTest", testWildcard)));

            /* 
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcEIP158ToByzantium", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcFrontierToHomestead", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcHomesteadToDao", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcHomesteadToEIP150", testWildcard));
            */
        }
    }
}