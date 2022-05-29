using System;
using System.Diagnostics;

namespace Imapp.Measurement.Runner
{
    class Program
    {
        static void Main(string[] args)
        {
            var bytecode = "62FFFFFF600020";
            var sampleSize = 1;

            if (args.Length >= 1)
            {
                bytecode = args[0];
            }

            if (args.Length >= 2)
            {
                int.TryParse(args[1], out sampleSize);
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

            for (int i = 0; i < sampleSize; ++i)
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
        }
    }
}
