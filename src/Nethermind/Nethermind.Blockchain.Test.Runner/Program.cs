// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private static double _totalMs;

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
                    await RunAllStateTests(testWildcard, s => new PerfStateTest(s));
#endif
                }
                else if (command == "b")
                {
                    stopwatch.Start();
                    await RunAllStateTests(testWildcard, s => new StateTestsBugHunter(s));
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

        private static async Task WrapAndRunDirectoryStateTests(IStateTestRunner stateTest)
        {
            var result = (await stateTest.RunTests()).ToList();
            var failedTestsInCategory = result.Where(r => !r.Pass).Select(t => t.Name + " " + t.LoadFailure).ToArray();
            AllFailingTests.AddRange(failedTestsInCategory);
            long categoryTimeInMs = (long)result.Sum(t => t.TimeInMs);
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
            long categoryTimeInMs = (long)testResults.Sum(t => t.TimeInMs);
            _totalMs += testResults.Sum(t => t.TimeInMs);

            if (testResults.Any())
            {
                Console.WriteLine($"CATEGORY {categoryTimeInMs}ms, FAILURES {failedTestsInCategory.Length}");
                Console.WriteLine();
            }
        }

        private static async Task RunAllStateTests(string testWildcard, Func<ITestSourceLoader, IStateTestRunner> testRunnerBuilder)
        {
            // do not loop over a list of directories so it is more convenient to rapidly comment out / comment in
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stArgsZeroOneBalance", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stAttackTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stBadOpcode", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stBugs", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCallCodes", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCallCreateCallCodeTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCallDelegateCodesCallCodeHomestead", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCallDelegateCodesHomestead", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stChainId", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stChangedEIP150", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCodeCopyTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCodeSizeLimit", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCreate2", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stCreateTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stDelegatecallTestHomestead", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stEIP150singleCodeGasPrices", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stEIP150Specific", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stEIP158Specific", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stEIP2930", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stExample", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stExtCodeHash", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stHomesteadSpecific", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stInitCodeTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stLogTests", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stMemExpandingEIP150Calls", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stMemoryStressTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stMemoryTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stNonZeroCallsTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stPreCompiledContracts", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stPreCompiledContracts2", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stQuadraticComplexityTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stRandom", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stRandom2", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stRecursiveCreate", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stRefundTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stReturnDataTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stRevertTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSelfBalance", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stShift", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSolidityTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSpecialTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSStoreTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stStackTests", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stStaticCall", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stStaticFlagEnabled", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSubroutine", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stSystemOperationsTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stTransactionTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stTransitionTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stWalletTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stZeroCallsRevert", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stZeroCallsTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stZeroKnowledge", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "stZeroKnowledge2", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "vmArithmeticTest", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "vmBitwiseLogicOperation", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "vmIOandFlowOperations", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "vmLogTests", testWildcard)));
            await WrapAndRunDirectoryStateTests(testRunnerBuilder(new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), "vmTests", testWildcard)));

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
