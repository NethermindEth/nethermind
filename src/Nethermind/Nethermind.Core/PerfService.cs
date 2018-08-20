/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core.Logging;

namespace Nethermind.Core
{
    public class PerfService : IPerfService
    {
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<Guid, Stopwatch> _stopwatches = new ConcurrentDictionary<Guid, Stopwatch>();

        public PerfService(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
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
                if (_logger.IsDebugEnabled) _logger.Debug($"PerfCalc: {logMsg}, time: {watch.ElapsedMilliseconds} milis");
            }
            else
            {
                if (_logger.IsWarnEnabled) _logger.Warn($"Stopwatch cannot be found in dict: {id}");
            }
        }
    }
}