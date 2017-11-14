using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ethereum.Blockchain.Test;

namespace Nevermind.Blockchain.Test.Runner
{
    public class PerfTest : BlockchainTestBase
    {
        public CategoryResult RunTests(string subset, int iterations = 1)
        {
            List<string> failingTests = new List<string>();
            long totalMs = 0L;
            Console.WriteLine($"RUNNING {subset}");
            Stopwatch stopwatch = new Stopwatch();
            IEnumerable<BlockchainTest> tests = LoadTests(subset);
            foreach (BlockchainTest test in tests)
            {
                stopwatch.Reset();
                for (int i = 0; i < iterations; i++)
                {
                    Setup();
                    try
                    {
                        RunTest(test, stopwatch);
                    }
                    catch (Exception e)
                    {
                        failingTests.Add(test.Name);
                        ConsoleColor mem = Console.ForegroundColor;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  EXCEPTION: {e.GetType().Name}");
                        Console.ForegroundColor = mem;
                    }
                }

                long ns = 1_000_000_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
                long ms = 1_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
                totalMs += ms;
                Console.WriteLine($"  {test.Name,-80}{ns / iterations,14}ns{ms / iterations,8}ms");
            }

            return new CategoryResult(totalMs, failingTests.ToArray());
        }
    }
}