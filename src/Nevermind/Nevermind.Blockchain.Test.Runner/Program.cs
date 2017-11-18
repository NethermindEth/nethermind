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

        private static void Run(ITestInRunner test, string category, int iterations = StandardIterations)
        {
            CategoryResult result = test.RunTests(category, iterations);

            AllFailingTests.AddRange(result.FailingTests);
            _totalMs += result.TotalMs;

            Console.WriteLine($"CATEGORY {category} {result.TotalMs}ms, FAILURES {result.FailingTests.Length}");
            foreach (string failingTest in result.FailingTests)
            {
                ConsoleColor mem = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  FAILED: {failingTest}");
                Console.ForegroundColor = mem;
            }

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine();
        }

        private static void Main(string[] args)
        {
            while (true)
            {
                Console.WriteLine("P/B");
                string r = Console.ReadLine();

                ShouldLog.Evm = false;
                ShouldLog.State = false;
                ShouldLog.TransactionProcessor = false;
                if (r == "p")
                {
                    Run(new PerfTest());
                }
                else
                {
                    Run(new BugHunter());
                }

                Console.WriteLine($"FINISHED {_totalMs}ms, FAILURES {AllFailingTests.Count}");
                foreach (string failingTest in AllFailingTests)
                {
                    ConsoleColor mem = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  FAILED: {failingTest}");
                    Console.ForegroundColor = mem;
                }

                Console.WriteLine("Press ENTER to continue");
                Console.ReadLine();
            }
        }

        private static void Run(ITestInRunner bugHunter)
        {
            Run(bugHunter, "stAttackTest");
            Run(bugHunter, "stBadOpcode");
            Run(bugHunter, "stCallCodes");
            Run(bugHunter, "stCallCreateCallCodeTest");
            Run(bugHunter, "stCallDelegateCodesCallCodeHomestead");
            Run(bugHunter, "stCallDelegateCodesHomestead");
            Run(bugHunter, "stChangedEIP150");
            Run(bugHunter, "stCodeCopyTest");
            Run(bugHunter, "stCodeSizeLimit");
            Run(bugHunter, "stCreateTest");
            Run(bugHunter, "stDelegatecallTestHomestead");
            Run(bugHunter, "stEIP150singleCodeGasPrices");
            Run(bugHunter, "stEIP150Specific");
            Run(bugHunter, "stEIP158Specific");
            Run(bugHunter, "stExample");
            Run(bugHunter, "stHomesteadSpecific");
            Run(bugHunter, "stInitCodeTest");
            Run(bugHunter, "stLogTests");
            Run(bugHunter, "stMemExpandingEIP150Calls");
            Run(bugHunter, "stMemoryStressTest");
            Run(bugHunter, "stMemoryTest");
            Run(bugHunter, "stNonZeroCallsTest");
            Run(bugHunter, "stPreCompiledContracts");
            Run(bugHunter, "stPreCompiledContracts2");
            Run(bugHunter, "stQuadraticComplexityTest");
            Run(bugHunter, "stRandom");
            Run(bugHunter, "stRandom2");
            Run(bugHunter, "stRecursiveCreate");
            Run(bugHunter, "stRefundTest");
            Run(bugHunter, "stReturnDataTest");
            Run(bugHunter, "stRevertTest");
            Run(bugHunter, "stSolidityTest");
            Run(bugHunter, "stSpecialTest");
            Run(bugHunter, "stStackTests");
            Run(bugHunter, "stStaticCall");
            Run(bugHunter, "stSystemOperationsTest");
            Run(bugHunter, "stTransactionTest");
            Run(bugHunter, "stTransitionTest");
            Run(bugHunter, "stWalletTest");
            Run(bugHunter, "stZeroCallsRevert");
            Run(bugHunter, "stZeroCallsTest");
            //Run("stZeroKnowledge");
            //Run("stZeroKnowledge2");
        }
    }
}