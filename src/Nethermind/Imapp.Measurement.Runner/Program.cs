using System;
using System.Diagnostics;
using System.CommandLine;

namespace Imapp.Measurement.Runner
{
    class Program
    {
        static int Main(string bytecode, int sampleSize = 1, bool printCSV = false)
        {
            if (String.IsNullOrEmpty(bytecode))
            {
                throw new Exception("Bytecode cannot be empty");
            }

            var runner = new EvmByteCodeBenchmark();
            runner.GlobalSetup(bytecode);

            //warmup
            for (int i = 0; i < 5; ++i)
            {
                runner.Setup();
                runner.ExecuteCode();
                runner.Cleanup();
            }

            var sw = new Stopwatch();
            sw.Start();
            var logger = new InstrumentationLogger();

            for (int i = 1; i <= sampleSize; ++i)
            {
                var loggerItem = new InstrumentationLoggerItem() { SampleId = i };
                runner.Setup();

                loggerItem.Start = sw.ElapsedTicks;
                runner.ExecuteCode();
                loggerItem.End = sw.ElapsedTicks;

                runner.Cleanup();
                logger.Add(loggerItem);
            }
            sw.Stop();

            logger.PrintResults();

            return 0;
        }
    }
}
