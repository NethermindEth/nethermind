using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Imapp.Measurement.Runner
{
    public class InstrumentationLogger
    {
        private List<InstrumentationLoggerItem> items = new List<InstrumentationLoggerItem>();

        public void Add(InstrumentationLoggerItem item)
        {
            if (item.End < item.Start) throw new Exception("Invalid measurement");
            items.Add(item);
        }

        public void PrintResults()
        {
            foreach (var item in items)
            {
                var ticksLength = item.End - item.Start;

                Console.WriteLine($"{item.SampleId},{TicksToNs(ticksLength)}");
            }
        }

        private double TicksToNs(long ticks)
        {
            double ns = 1000000000.0 * (double)ticks / Stopwatch.Frequency;
            return ns;
        }
    }

    public struct InstrumentationLoggerItem
    {
        public int SampleId { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
    }
}
