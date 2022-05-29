using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;

namespace Imapp.Benchmark.Runner
{
    public class BenchmarkNullExporter : IExporter
    {
        public string Name => "ImappNullExporter";

        public IEnumerable<string> ExportToFiles(Summary summary, ILogger consoleLogger)
        {
            return Enumerable.Empty<string>();

            // Debugging code

            //Console.WriteLine("Exporter Code: " + Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE"));
            //var s = summary.Reports.Select(r =>
            //{
            //    var a = $"Exporter N: {r.ResultStatistics.N}, Median: {r.ResultStatistics.Median}";
            //    Console.WriteLine(a);
            //    return a;
            //});
            //return s;
        }

        public void ExportToLog(Summary summary, ILogger logger)
        {
        }
    }
}
