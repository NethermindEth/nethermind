using System;
using System.Collections.Generic;
using Nevermind.Evm;

namespace Nevermind.Blockchain.Test.Runner
{
    internal class Program
    {
        private const int StandardIterations = 1;

        private static readonly PerfTest PerfTest = new PerfTest();
        private static readonly List<string> AllFailingTests = new List<string>();
        private static long _totalMs;

        private static void Run(string category, int iterations = StandardIterations)
        {
            CategoryResult result = PerfTest.RunTests(category, iterations);
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
            //ShouldLog.Evm = false;
            //ShouldLog.TransactionProcessor = false;
            //ShouldLog.State = false;

            Run("stAttackTest");
            Run("stBadOpcode");
            Run("stCallCodes");
            Run("stCallCreateCallCodeTest");
            Run("stCallDelegateCodesCallCodeHomestead");
            Run("stCallDelegateCodesHomestead");
            Run("stChangedEIP150");
            Run("stCodeCopyTest");
            Run("stCodeSizeLimit");
            Run("stCreateTest");
            Run("stDelegatecallTestHomestead");
            Run("stEIP150singleCodeGasPrices");
            Run("stEIP150Specific");
            Run("stEIP158Specific");
            Run("stExample");
            Run("stHomesteadSpecific");
            Run("stInitCodeTest");
            Run("stLogTests");
            Run("stMemExpandingEIP150Calls");
            Run("stMemoryStressTest");
            Run("stMemoryTest");
            Run("stNonZeroCallsTest");
            Run("stPreCompiledContracts");
            Run("stPreCompiledContracts2");
            Run("stQuadraticComplexityTest");
            Run("stRandom");
            Run("stRandom2");
            Run("stRecursiveCreate");
            Run("stRefundTest");
            Run("stReturnDataTest");
            Run("stRevertTest");
            Run("stSolidityTest");
            Run("stSpecialTest");
            Run("stStackTests");
            Run("stStaticCall");
            Run("stSystemOperationsTest");
            Run("stTransactionTest");
            Run("stTransitionTest");
            Run("stWalletTest");
            Run("stZeroCallsRevert");
            Run("stZeroCallsTest");
            Run("stZeroKnowledge");
            Run("stZeroKnowledge2");

            Console.WriteLine($"FINISHED {_totalMs}ms, FAILURES {AllFailingTests.Count}");
            foreach (string failingTest in AllFailingTests)
            {
                ConsoleColor mem = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  FAILED: {failingTest}");
                Console.ForegroundColor = mem;
            }

            Console.ReadLine();
        }
    }
}