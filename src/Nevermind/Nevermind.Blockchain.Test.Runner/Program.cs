using System;
using System.Collections.Generic;
using Nevermind.Evm;

namespace Nevermind.Blockchain.Test.Runner
{
    internal class Program
    {
        private const int StandardIterations = 1;

        private static readonly List<string> AllFailingTests = new List<string>();
        private static long _totalMs;

        private static void Run(ITestInRunner test, string category, string testWildcard, int iterations = StandardIterations)
        {
            CategoryResult result = test.RunTests(category, testWildcard, iterations);

            AllFailingTests.AddRange(result.FailingTests);
            _totalMs += result.TotalMs;

            Console.WriteLine($"CATEGORY {category} {result.TotalMs}ms, FAILURES {result.FailingTests.Length}");
//            foreach (string failingTest in result.FailingTests)
//            {
//                ConsoleColor mem = Console.ForegroundColor;
//                Console.ForegroundColor = ConsoleColor.Red;
//                Console.WriteLine($"  FAILED: {failingTest}");
//                Console.ForegroundColor = mem;
//            }

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine();
        }

        private static void Main(string[] args)
        {
            while (true)
            {
                Console.WriteLine("P/B");
                string[] input = Console.ReadLine().Split();
                string command = input[0];
                string testWildcard = input.Length <= 1 ? null : input[1];

                if (command == "p")
                {
                    ShouldLog.Evm = false;
                    ShouldLog.State = false;
                    ShouldLog.EvmStack = false;
                    ShouldLog.TransactionProcessor = false;
                    Run(new PerfTest(), testWildcard);
                }
                else
                {
                    ShouldLog.Evm = true;
                    ShouldLog.State = true;
                    ShouldLog.EvmStack = false;
                    ShouldLog.TransactionProcessor = true;
                    Run(new BugHunter(), testWildcard);
                }

                foreach (string failingTest in AllFailingTests)
                {
                    ConsoleColor mem = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  FAILED: {failingTest}");
                    Console.ForegroundColor = mem;
                }

                Console.WriteLine($"FINISHED {_totalMs}ms, FAILURES {AllFailingTests.Count}");
                Console.WriteLine("Press ENTER to continue");
                AllFailingTests.Clear();
                Console.ReadLine();
            }
        }

        private static void Run(ITestInRunner bugHunter, string testWildcard)
        {
            //Run(bugHunter, "stAttackTest");
            //Run(bugHunter, "stBadOpcode");
            //Run(bugHunter, "stCallCodes");
            //Run(bugHunter, "stCallCreateCallCodeTest");
            //Run(bugHunter, "stCallDelegateCodesCallCodeHomestead");
            //Run(bugHunter, "stCallDelegateCodesHomestead");
            //Run(bugHunter, "stChangedEIP150");
            //Run(bugHunter, "stCodeCopyTest");
            //Run(bugHunter, "stCodeSizeLimit");
            //Run(bugHunter, "stCreateTest");
            //Run(bugHunter, "stDelegatecallTestHomestead");
            //Run(bugHunter, "stEIP150singleCodeGasPrices");
            //Run(bugHunter, "stEIP150Specific");
            //Run(bugHunter, "stEIP158Specific");
            //Run(bugHunter, "stExample");
            //Run(bugHunter, "stHomesteadSpecific");
            //Run(bugHunter, "stInitCodeTest");
            //Run(bugHunter, "stLogTests");
            //Run(bugHunter, "stMemExpandingEIP150Calls");
            //Run(bugHunter, "stMemoryStressTest");
            //Run(bugHunter, "stMemoryTest");
            //Run(bugHunter, "stNonZeroCallsTest");
            //Run(bugHunter, "stPreCompiledContracts");
            //Run(bugHunter, "stPreCompiledContracts2");
            //Run(bugHunter, "stQuadraticComplexityTest");
            //Run(bugHunter, "stRandom");
            //Run(bugHunter, "stRandom2");
            //Run(bugHunter, "stRecursiveCreate");
            //Run(bugHunter, "stRefundTest");
            //Run(bugHunter, "stReturnDataTest");
            //Run(bugHunter, "stRevertTest");
            //Run(bugHunter, "stSolidityTest");
            //Run(bugHunter, "stSpecialTest");
            //Run(bugHunter, "stStackTests");
            //Run(bugHunter, "stStaticCall");
            //Run(bugHunter, "stSystemOperationsTest");
            //Run(bugHunter, "stTransactionTest");
            //Run(bugHunter, "stTransitionTest");
            //Run(bugHunter, "stWalletTest");
            //Run(bugHunter, "stZeroCallsRevert");
            //Run(bugHunter, "stZeroCallsTest");

            Run(bugHunter, "bcBlockGasLimitTest", testWildcard);
            Run(bugHunter, "bcExploitTest", testWildcard);
            Run(bugHunter, "bcForgedTest", testWildcard);
            Run(bugHunter, "bcForkStressTest", testWildcard);
            Run(bugHunter, "bcGasPricerTest", testWildcard);
            Run(bugHunter, "bcInvalidHeaderTest", testWildcard);
            Run(bugHunter, "bcMultiChainTestTest", testWildcard);
            Run(bugHunter, "bcRandomBlockhashTest", testWildcard);
            Run(bugHunter, "bcStateTests", testWildcard);
            Run(bugHunter, "bcTotalDifficultyTest", testWildcard);
            Run(bugHunter, "bcUncleHeaderValidity", testWildcard);
            Run(bugHunter, "bcUncleTest", testWildcard);
            Run(bugHunter, "bcValidBlockTest", testWildcard);
            Run(bugHunter, "bcWalletTest", testWildcard);
            
            //Run("stZeroKnowledge");
            //Run("stZeroKnowledge2");
        }
    }
}