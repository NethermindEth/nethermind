using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ethereum.Blockchain.Test;

namespace Nevermind.Blockchain.Test.Runner
{
    public class PerfTest : BlockchainTestBase
    {
        public long RunTests(string subset, int iterations = 1)
        {
            long totalMs = 0L;
            Console.WriteLine($"RUNNING {subset}");
            Stopwatch stopwatch = new Stopwatch();
            IEnumerable<BlockchainTest> test = LoadTests(subset);
            foreach (BlockchainTest blockchainTest in test)
            {
                stopwatch.Reset();
                for (int i = 0; i < iterations; i++)
                {
                    Setup();
                    try
                    {
                        RunTest(blockchainTest, stopwatch);
                    }
                    catch (Exception e)
                    {
                        ConsoleColor mem = Console.ForegroundColor;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  EXCEPTION: {e}");
                        Console.ForegroundColor = mem;
                    }
                }
                
                long ns = 1_000_000_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
                long ms = 1_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
                totalMs += ms;
                Console.WriteLine($"  {blockchainTest.Name, -80}{ns / iterations, 14}ns{ms / iterations, 8}ms");
            }

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine();
            return totalMs;
        }
    }
}