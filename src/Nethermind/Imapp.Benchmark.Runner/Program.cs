using System;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Imapp.Benchmark.Runner
{
    class Program
    {
        static void Main(string[] args)
        {
            var config = new ManualConfig()
                .WithOptions(ConfigOptions.DisableOptimizationsValidator)
                .WithOptions(ConfigOptions.DisableLogFile)
                //.AddValidator(JitOptimizationsValidator.DontFailOnError)
                .AddLogger(new BenchmarkNullLogger())
                //.AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default)
                .AddExporter(new BenchmarkNullExporter())
                ;

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
        }

        private static void OutputOverheadResults(int sampleId, BenchmarkDotNet.Reports.Summary rEmpty, BenchmarkDotNet.Reports.Summary rActual)
        {
            var reportEmpty = rEmpty.Reports[0];
            var reportActual = rActual.Reports[0];
            var overheadTime = reportEmpty.ResultStatistics.Mean;

            var loopExecutionTime = Math.Max(reportActual.ResultStatistics.Mean - reportEmpty.ResultStatistics.Mean, 0);
            var totalTime = Math.Max(reportActual.ResultStatistics.Mean, reportEmpty.ResultStatistics.Mean);

            var memAllocPerOp = reportActual.GcStats.GetBytesAllocatedPerOperation(reportActual.BenchmarkCase);

            Console.WriteLine($"{sampleId},{reportActual.ResultStatistics.N},{overheadTime},{loopExecutionTime},{totalTime},{reportActual.ResultStatistics.StandardDeviation},{reportActual.GcStats.TotalOperations},{memAllocPerOp}");
        }
    }
}
