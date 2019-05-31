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
using System.Diagnostics;
using Nethermind.Logging;

namespace Nethermind.Core
{
    [Todo(Improve.Refactor, "Remove this class and replce with an utility with IDisposable")]
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
            _stopwatches[id] = Stopwatch.StartNew();
            return id;
        }

        public void EndPerfCalc(Guid id, string logMsg)
        {
            if (_stopwatches.TryRemove(id, out var watch))
            {
                watch.Stop();
                if (LogOnDebug)
                {
                    if (_logger.IsDebug) _logger.Debug($"PerfCalc: {logMsg}, time: {watch.ElapsedMilliseconds} milis");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"PerfCalc: {logMsg}, time: {watch.ElapsedMilliseconds} milis");
                }
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Stopwatch cannot be found in dict: {id}");
            }
        }

        public long? EndPerfCalc(Guid id)
        {
            if (_stopwatches.TryRemove(id, out var watch))
            {
                watch.Stop();
                return watch.ElapsedMilliseconds;
            }

            if (_logger.IsWarn) _logger.Warn($"Stopwatch cannot be found in dict: {id}");
            return null;
        }

        public bool LogOnDebug { get; set; }
    }
}