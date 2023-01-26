// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using System.Linq;
using BenchmarkDotNet.Filters;
using System;

namespace Nethermind.Benchmark.Runner
{
    public class DashboardConfig : ManualConfig
    {
        public DashboardConfig(IEnumerable<string> filters, params Job[] jobs)
        {
            foreach (Job job in jobs)
            {
                AddJob(job);
            }

            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Statistics);
            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Params);
            AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
            AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.FullCompressed);
            AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
            WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(100));

            if (filters.Any()) 
            {
                IFilter[] nameFilters = filters.Select(a => new SimpleFilter(c => c.Parameters.Items.Any(p=>p.Value.ToString().Contains(a)))).OfType<IFilter>().ToArray();
                AddFilter(new DisjunctionFilter(nameFilters));
            }
        }
    }

    public class NoOutputConfig : ManualConfig
    {
        public NoOutputConfig(IEnumerable<string> filters, params Job[] jobs)
        {
            foreach (Job job in jobs)
            {
                AddJob(job);
            }

            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Statistics);
            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Params);
            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Metrics);
            AddLogger(new BenchmarkNullLogger());
            AddExporter(new BenchmarkNullExporter());
            AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);

            if (filters.Any()) 
            {
                IFilter[] nameFilters = filters.Select(a => new SimpleFilter(c => c.Parameters.Items.Any(p=>p.Value.ToString().Contains(a)))).OfType<IFilter>().ToArray();
                AddFilter(new DisjunctionFilter(nameFilters));
            }
        }
    }

    public class BenchmarkNullLogger : ILogger
    {
        public string Id => "ImappNullLogger";

        public int Priority => 1;

        public void Flush()
        {
        }

        public void Write(LogKind logKind, string text)
        {
        }

        public void WriteLine()
        {
        }

        public void WriteLine(LogKind logKind, string text)
        {
        }
    }

    public class ExportComparisonLogger : BenchmarkDotNet.Loggers.ILogger
    {
        private bool skipEmptyLine = false;

        public string Id => "ExportComparisonLogger";

        public int Priority => 1;

        public void Flush()
        {
        }

        public void Write(BenchmarkDotNet.Loggers.LogKind logKind, string text)
        {
            if (logKind == BenchmarkDotNet.Loggers.LogKind.Statistic) 
            {
                Console.Write(text);
                skipEmptyLine = false;
            }
        }

        public void WriteLine()
        {
            if (!skipEmptyLine) 
            {
                Console.WriteLine();
                skipEmptyLine = true;
            }
        }

        public void WriteLine(BenchmarkDotNet.Loggers.LogKind logKind, string text)
        {
            if (
                logKind == BenchmarkDotNet.Loggers.LogKind.Statistic && 
                (text.StartsWith("|") || text.StartsWith("-"))) 
                {
                    Console.WriteLine(text);
                    skipEmptyLine = false;
                }
        }
    }
    
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
