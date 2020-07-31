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
using Ethereum.Test.Base.Interfaces;

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

        private static void RunAllStateTests(string testWildcard, Func<ITestSourceLoader, IStateTestRunner> testRunnerBuilder)
        {
            // do not loop over a list of directories so it is more convenient to rapidly comment out / comment in
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stArgsZeroOneBalance", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stAttackTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stBadOpcode", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stBugs", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stCallCodes", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stCallCreateCallCodeTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stCallDelegateCodesCallCodeHomestead", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stCallDelegateCodesHomestead", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stChangedEIP150", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stCodeCopyTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stCodeSizeLimit", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stCreate2", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stCreateTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stDelegatecallTestHomestead", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stEIP150singleCodeGasPrices", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stEIP150Specific", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stEIP158Specific", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stExample", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stHomesteadSpecific", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stInitCodeTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stLogTests", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stMemExpandingEIP150Calls", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stMemoryStressTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stMemoryTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stNonZeroCallsTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stPreCompiledContracts", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stPreCompiledContracts2", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stQuadraticComplexityTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stRandom", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stRandom2", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stRecursiveCreate", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stRefundTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stReturnDataTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stRevertTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stShift", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stSolidityTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stSpecialTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stStackTests", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stStaticCall", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stSystemOperationsTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stTransactionTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stTransitionTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stWalletTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stZeroCallsRevert", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stZeroCallsTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stZeroKnowledge", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(),"stZeroKnowledge2", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcBlockGasLimitTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcExploitTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcForgedTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcForkStressTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcGasPricerTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcInvalidHeaderTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcMultiChainTestTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcRandomBlockhashTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcStateTests", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcTotalDifficultyTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcUncleHeaderValidity", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcUncleTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcValidBlockTest", testWildcard)));
            WrapAndRunDirectoryTests(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(),"bcWalletTest", testWildcard)));

            /* 
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcEIP158ToByzantium", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcFrontierToHomestead", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcHomesteadToDao", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcHomesteadToEIP150", testWildcard));
            */
        }
    }
}