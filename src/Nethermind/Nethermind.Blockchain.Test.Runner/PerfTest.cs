// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Test.Runner
{
    public class PerfStateTest : GeneralStateTestBase, IStateTestRunner
    {
        private readonly ITestSourceLoader _testsSource;

        public PerfStateTest(ITestSourceLoader testsSource)
        {
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
        }

        public IEnumerable<EthereumTestResult> RunTests()
        {
            List<EthereumTestResult> results = new List<EthereumTestResult>();
            Console.WriteLine($"RUNNING tests");
            Stopwatch stopwatch = new Stopwatch();
            IEnumerable<GeneralStateTest> tests = (IEnumerable<GeneralStateTest>)_testsSource.LoadTests();
            bool isNewLine = true;
            foreach (GeneralStateTest test in tests)
            {
                if (test.LoadFailure != null)
                {
                    continue;
                }

                Setup(LimboLogs.Instance);
                stopwatch.Restart();
                EthereumTestResult result = RunTest(test);
                stopwatch.Stop();
                results.Add(result);

                if (!result.Pass)
                {
                    ConsoleColor mem = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    if (!isNewLine)
                    {
                        Console.WriteLine();
                        isNewLine = true;
                    }

                    Console.WriteLine($"  {test.Name,-80} FAIL");
                    Console.ForegroundColor = mem;
                }

                long ns = 1_000_000_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
                long ms = 1_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
                if (ms > 100)
                {
                    if (!isNewLine)
                    {
                        Console.WriteLine();
                        isNewLine = true;
                    }

                    Console.WriteLine($"  {test.Name,-80}{ns,14}ns{ms,8}ms");
                }
                else
                {
                    Console.Write(".");
                    isNewLine = false;
                }
            }

            if (!isNewLine)
            {
                Console.WriteLine();
            }

            return results;
        }
    }
}
