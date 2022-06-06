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
        }

        public void ExportToLog(Summary summary, ILogger logger)
        {
        }
    }
}
