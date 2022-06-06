using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Loggers;

namespace Imapp.Benchmark.Runner
{
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
}
