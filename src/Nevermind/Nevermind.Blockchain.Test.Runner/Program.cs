using System;
using System.Diagnostics;
using Nevermind.Evm;

namespace Nevermind.Blockchain.Test.Runner
{
    internal class Program
    {
        private const int StandardIterations = 1;

        private static void Main(string[] args)
        {
            long totalMs = 0L;
            ShouldLog.Evm = false;
            ShouldLog.TransactionProcessor = false;
            PerfTest perfTest = new PerfTest();
            //totalMs += perfTest.RunTests("stAttackTest");
            totalMs += perfTest.RunTests("stBadOpcode", StandardIterations);
            totalMs += perfTest.RunTests("stCallCodes", StandardIterations);
            totalMs += perfTest.RunTests("stCallCreateCallCodeTest", StandardIterations);
            totalMs += perfTest.RunTests("stCallDelegateCodesCallCodeHomestead", StandardIterations);
            totalMs += perfTest.RunTests("stCallDelegateCodesHomestead", StandardIterations);
            //totalMs += perfTest.RunTests("stChangedEIP150", StandardIterations);
            totalMs += perfTest.RunTests("stCodeCopyTest", StandardIterations);
            totalMs += perfTest.RunTests("stCodeSizeLimit", StandardIterations);
            totalMs += perfTest.RunTests("stCreateTest", StandardIterations);
            totalMs += perfTest.RunTests("stDelegatecallTestHomestead", StandardIterations);
            //totalMs += perfTest.RunTests("stEIP150singleCodeGasPrices", StandardIterations);
            //totalMs += perfTest.RunTests("stEIP150Specific", StandardIterations);
            //totalMs += perfTest.RunTests("stEIP158Specific", StandardIterations);
            totalMs += perfTest.RunTests("stExample", StandardIterations);
            totalMs += perfTest.RunTests("stHomesteadSpecific", StandardIterations);
            totalMs += perfTest.RunTests("stInitCodeTest", StandardIterations);
            totalMs += perfTest.RunTests("stLogTests", StandardIterations);
            //totalMs += perfTest.RunTests("stMemExpandingEIP150Calls", StandardIterations);
            totalMs += perfTest.RunTests("stMemoryStressTest", StandardIterations);
            totalMs += perfTest.RunTests("stMemoryTest", StandardIterations);
            totalMs += perfTest.RunTests("stNonZeroCallsTest", StandardIterations);
            totalMs += perfTest.RunTests("stPreCompiledContracts", StandardIterations);
            totalMs += perfTest.RunTests("stPreCompiledContracts2", StandardIterations);
            //totalMs += perfTest.RunTests("stQuadraticComplexityTest", StandardIterations);
            totalMs += perfTest.RunTests("stRandom", StandardIterations);
            totalMs += perfTest.RunTests("stRandom2", StandardIterations);
            //totalMs += perfTest.RunTests("stRecursiveCreate", StandardIterations);
            totalMs += perfTest.RunTests("stRefundTest", StandardIterations);
            totalMs += perfTest.RunTests("stReturnDataTest", StandardIterations);
            totalMs += perfTest.RunTests("stRevertTest", StandardIterations);
            totalMs += perfTest.RunTests("stSolidityTest", StandardIterations);
            totalMs += perfTest.RunTests("stSpecialTest", StandardIterations);
            totalMs += perfTest.RunTests("stStackTests", StandardIterations);
            totalMs += perfTest.RunTests("stStaticCall", StandardIterations);
            totalMs += perfTest.RunTests("stSystemOperationsTest", StandardIterations);
            totalMs += perfTest.RunTests("stTransactionTest", StandardIterations);
            //totalMs += perfTest.RunTests("stTransitionTest", StandardIterations);
            //totalMs += perfTest.RunTests("stWalletTest", StandardIterations);
            totalMs += perfTest.RunTests("stZeroCallsRevert", StandardIterations);
            totalMs += perfTest.RunTests("stZeroCallsTest", StandardIterations);
            //totalMs += perfTest.RunTests("stZeroKnowledge", StandardIterations);
            //totalMs += perfTest.RunTests("stZeroKnowledge2", StandardIterations);
            Console.WriteLine($"FINISHED in {totalMs}ms");
            Console.ReadLine();
        }
    }
}