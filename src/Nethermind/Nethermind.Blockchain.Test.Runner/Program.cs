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
using Nethermind.Evm;

namespace Nethermind.Blockchain.Test.Runner
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
                    ShouldLog.Processing = false;
                    Run(new PerfTest(), testWildcard);
                }
                else
                {
                    ShouldLog.Evm = true;
                    ShouldLog.State = true;
                    ShouldLog.EvmStack = false;
                    ShouldLog.Processing = true;
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
            Run(bugHunter, "stAttackTest", testWildcard);
            Run(bugHunter, "stBadOpcode", testWildcard);
            Run(bugHunter, "stCallCodes", testWildcard);
            Run(bugHunter, "stCallCreateCallCodeTest", testWildcard);
            Run(bugHunter, "stCallDelegateCodesCallCodeHomestead", testWildcard);
            Run(bugHunter, "stCallDelegateCodesHomestead", testWildcard);
            Run(bugHunter, "stChangedEIP150", testWildcard);
            Run(bugHunter, "stCodeCopyTest", testWildcard);
            Run(bugHunter, "stCodeSizeLimit", testWildcard);
            Run(bugHunter, "stCreateTest", testWildcard);
            Run(bugHunter, "stDelegatecallTestHomestead", testWildcard);
            Run(bugHunter, "stEIP150singleCodeGasPrices", testWildcard);
            Run(bugHunter, "stEIP150Specific", testWildcard);
            Run(bugHunter, "stEIP158Specific", testWildcard);
            Run(bugHunter, "stExample", testWildcard);
            Run(bugHunter, "stHomesteadSpecific", testWildcard);
            Run(bugHunter, "stInitCodeTest", testWildcard);
            Run(bugHunter, "stLogTests", testWildcard);
            Run(bugHunter, "stMemExpandingEIP150Calls", testWildcard);
            Run(bugHunter, "stMemoryStressTest", testWildcard);
            Run(bugHunter, "stMemoryTest", testWildcard);
            Run(bugHunter, "stNonZeroCallsTest", testWildcard);
            Run(bugHunter, "stPreCompiledContracts", testWildcard);
            Run(bugHunter, "stPreCompiledContracts2", testWildcard);
            Run(bugHunter, "stQuadraticComplexityTest", testWildcard);
            Run(bugHunter, "stRandom", testWildcard);
            Run(bugHunter, "stRandom2", testWildcard);
            Run(bugHunter, "stRecursiveCreate", testWildcard);
            Run(bugHunter, "stRefundTest", testWildcard);
            Run(bugHunter, "stReturnDataTest", testWildcard);
            Run(bugHunter, "stRevertTest", testWildcard);
            Run(bugHunter, "stSolidityTest", testWildcard);
            Run(bugHunter, "stSpecialTest", testWildcard);
            Run(bugHunter, "stStackTests", testWildcard);
            Run(bugHunter, "stStaticCall", testWildcard);
            Run(bugHunter, "stSystemOperationsTest", testWildcard);
            Run(bugHunter, "stTransactionTest", testWildcard);
            Run(bugHunter, "stTransitionTest", testWildcard);
            Run(bugHunter, "stWalletTest", testWildcard);
            Run(bugHunter, "stZeroCallsRevert", testWildcard);
            Run(bugHunter, "stZeroCallsTest", testWildcard);

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

            Run(bugHunter, "stZeroKnowledge", testWildcard);
            Run(bugHunter,"stZeroKnowledge2", testWildcard);
        }
    }
}