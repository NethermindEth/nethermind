using System;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using System.CommandLine;

namespace Imapp.Benchmark.Runner
{
    class Program
    {
        static int Main(string bytecode, int sampleSize = 1, bool printCSV = false)
        {
            var config = new ManualConfig()
                //.WithOptions(ConfigOptions.DisableOptimizationsValidator)
                .WithOptions(ConfigOptions.DisableLogFile)
                .AddExporter(new BenchmarkNullExporter());

            if (printCSV)
            {
                config = config.AddLogger(new BenchmarkNullLogger());
            }
            else
            {
                config = config.AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
            }

            if (String.IsNullOrEmpty(bytecode))
            {
                throw new Exception("Bytecode cannot be empty");
            }

            for (int i = 1; i <= sampleSize; ++i)
            {
                Environment.SetEnvironmentVariable("NETH.BENCHMARK.BYTECODE", "00" + bytecode);
                //Console.WriteLine("Code: " + Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE"));
                var r1 = BenchmarkRunner.Run<EvmByteCodeBenchmark>(config);

                Environment.SetEnvironmentVariable("NETH.BENCHMARK.BYTECODE", bytecode);
                //Console.WriteLine("Code: " + Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE"));
                var r2 = BenchmarkRunner.Run<EvmByteCodeBenchmark>(config);

                OutputOverheadResults(i, r1, r2);
            }

            return 0;
        }

        private static void OutputOverheadResults(int sampleId, BenchmarkDotNet.Reports.Summary rEmpty, BenchmarkDotNet.Reports.Summary rActual)
        {
            var reportEmpty = rEmpty.Reports[0];
            var reportActual = rActual.Reports[0];
            var overheadTime = reportEmpty.ResultStatistics.Mean;

            var loopExecutionTime = reportActual.ResultStatistics.Mean - reportEmpty.ResultStatistics.Mean;
            var totalTime = reportActual.ResultStatistics.Mean;

            var memAllocPerOp = reportActual.GcStats.GetBytesAllocatedPerOperation(reportActual.BenchmarkCase);

            Console.WriteLine($"{sampleId},{reportActual.ResultStatistics.N},{overheadTime},{loopExecutionTime},{totalTime},{reportActual.ResultStatistics.StandardDeviation},{reportActual.GcStats.TotalOperations},{memAllocPerOp}");
        }
    }
}
