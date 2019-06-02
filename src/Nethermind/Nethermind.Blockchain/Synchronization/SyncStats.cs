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
using Nethermind.Logging;

namespace Nethermind.Blockchain.Synchronization
{
    internal class SyncStats
    {
        private ILogger _logger;
        private string _prefix;
        
        private bool _isFirst = true;
        private long _firstCurrent;
        private DateTime _firstNotificationTime = DateTime.MinValue;
        private DateTime _lastSyncNotificationTime = DateTime.MinValue;
        private long _lastCurrent;
        private long _lastTotal;

        public SyncStats(string prefix, ILogManager logManager)
        {
            _prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void Update(long current, long total, int usefulPeerCount)
        {
            // create sync stats like processing stats?
            if (DateTime.UtcNow - _lastSyncNotificationTime >= TimeSpan.FromSeconds(1)
                && (_lastCurrent != current || _lastTotal != total))
            {
                if (_logger.IsInfo)
                {
                    double bps = (current - _firstCurrent) / (DateTime.UtcNow - _firstNotificationTime).TotalSeconds;
                    string bpsString = _isFirst ? "N/A" : $"{bps:F2}bps";
                    double bpspp = bps/usefulPeerCount;
                    string bpsppString = _isFirst ? "N/A" : $"{bpspp:F2}bpspp";
                    if (current != _firstCurrent)
                    {
                        _logger.Info($"{_prefix.PadRight(7, ' ')} download        {current.ToString().PadLeft(9, ' ')}/{total} | {bpsString} | {bpsppString}");
                    }
                }

                _lastSyncNotificationTime = DateTime.UtcNow;
                _lastCurrent = current;
                _lastTotal = total;
            }

            if (_isFirst)
            {
                _firstCurrent = _lastCurrent;
                _firstNotificationTime = DateTime.UtcNow;
                _isFirst = false;
            }
        }
    }
}