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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;

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
                    RunAllStateTests(testWildcard, s => new PerfStateTest(s));
#endif
                }
                else if (command == "b")
                {
                    stopwatch.Start();
                    RunAllStateTests(testWildcard, s => new StateTestsBugHunter(s));
                    await RunAllBlockchainTestAsync(testWildcard, b => new BlockchainTestsBugHunter(b));
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

        private static void WrapAndRunDirectoryStateTests(IStateTestRunner stateTest)
        {
            var result = stateTest.RunTests().ToList();
            var failedTestsInCategory = result.Where(r => !r.Pass).Select(t => t.Name + " " + t.LoadFailure).ToArray();
            AllFailingTests.AddRange(failedTestsInCategory);
            long categoryTimeInMs = result.Sum(t => t.TimeInMs);
            _totalMs += result.Sum(t => t.TimeInMs);

            if (result.Any())
            {
                Console.WriteLine($"CATEGORY {categoryTimeInMs}ms, FAILURES {failedTestsInCategory.Length}");
                Console.WriteLine();
            }
        }

        private static async Task WrapAndRunDirectoryBlockchainTestsAsync(IBlockchainTestRunner blockchainTestRunner)
        {
            var result = await blockchainTestRunner.RunTestsAsync();
            var testResults = result.ToList();

            var failedTestsInCategory = testResults.Where(r => !r.Pass).Select(t => t.Name + " " + t.LoadFailure).ToArray();
            AllFailingTests.AddRange(failedTestsInCategory);
            long categoryTimeInMs = testResults.Sum(t => t.TimeInMs);
            _totalMs += testResults.Sum(t => t.TimeInMs);

            if (testResults.Any())
            {
                Console.WriteLine($"CATEGORY {categoryTimeInMs}ms, FAILURES {failedTestsInCategory.Length}");
                Console.WriteLine();
            }
        }

        private static void RunAllStateTests(string testWildcard, Func<ITestSourceLoader, IStateTestRunner> testRunnerBuilder)
        {
            // do not loop over a list of directories so it is more convenient to rapidly comment out / comment in
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stArgsZeroOneBalance", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stAttackTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stBadOpcode", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stBugs", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCallCodes", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCallCreateCallCodeTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCallDelegateCodesCallCodeHomestead", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCallDelegateCodesHomestead", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stChainId", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stChangedEIP150", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCodeCopyTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCodeSizeLimit", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCreate2", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCreateTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stDelegatecallTestHomestead", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stEIP150singleCodeGasPrices", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stEIP150Specific", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stEIP158Specific", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stEIP2930", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stExample", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stExtCodeHash", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stHomesteadSpecific", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stInitCodeTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stLogTests", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stMemExpandingEIP150Calls", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stMemoryStressTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stMemoryTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stNonZeroCallsTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stPreCompiledContracts", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stPreCompiledContracts2", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stQuadraticComplexityTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stRandom", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stRandom2", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stRecursiveCreate", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stRefundTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stReturnDataTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stRevertTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSelfBalance", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stShift", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSolidityTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSpecialTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSStoreTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stStackTests", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stStaticCall", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stStaticFlagEnabled", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSubroutine", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSystemOperationsTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stTransactionTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stTransitionTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stWalletTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stZeroCallsRevert", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stZeroCallsTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stZeroKnowledge", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stZeroKnowledge2", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "vmArithmeticTest", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "vmBitwiseLogicOperation", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "vmIOandFlowOperations", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "vmLogTests", testWildcard)));
            WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "vmTests", testWildcard)));

            /* 
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcEIP158ToByzantium", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcFrontierToHomestead", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcHomesteadToDao", testWildcard));
            await Run(testRunnerBuilder(new DirectoryTestsSource("bcHomesteadToEIP150", testWildcard));
            */
        }

        private static async Task RunAllBlockchainTestAsync(string testWildcard, Func<ITestSourceLoader, IBlockchainTestRunner> testRunnerBuilder)
        {
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcBlockGasLimitTest", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcExploitTest", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcForgedTest", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcForkStressTest", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcGasPricerTest", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcInvalidHeaderTest", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcMultiChainTestTest", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcRandomBlockhashTest", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcStateTests", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcTotalDifficultyTest", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcUncleHeaderValidity", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcUncleTest", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcValidBlockTest", testWildcard)));
            await WrapAndRunDirectoryBlockchainTestsAsync(testRunnerBuilder(new TestsSourceLoader(new LoadBlockchainTestsStrategy(), "bcWalletTest", testWildcard)));
        }
    }
}
