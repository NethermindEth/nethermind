using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace Nethermind.Core
{
    public class PerfService : IPerfService
    {
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<Guid, Stopwatch> _stopwatches = new ConcurrentDictionary<Guid, Stopwatch>();

        public PerfService(ILogger logger)
        {
            _logger = logger;
        }

        public Guid StartPerfCalc()
        {
            var id = Guid.NewGuid();
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            _stopwatches[id] = stopwatch;
            return id;
        }

        public void EndPerfCalc(Guid id, string logMsg)
        {
            if (_stopwatches.TryRemove(id, out var watch))
            {
                watch.Stop();
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"PerfCalc: {logMsg}, time: {watch.ElapsedMilliseconds} milis");
                }
            }
            else
            {
                if (_logger.IsErrorEnabled)
                {
                    _logger.Error($"Stopwatch cannot be found in dict: {id}");
                }
            }
        }
    }
}