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
        private long _firstCurrent;
        private DateTime _firstNotificationTime = DateTime.MinValue;
        private DateTime _lastSyncNotificationTime = DateTime.MinValue;
        private long _lastCurrent;
        private long _lastTotal;

        private ILogger _logger;

        public SyncStats(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private decimal _average = 0;
        private bool isFirst = true;

        public void ReportBlocksDownload(long current, long total)
        {
            // create sync stats like processing stats?
            if (DateTime.UtcNow - _lastSyncNotificationTime >= TimeSpan.FromSeconds(1)
                && (_lastCurrent != current || _lastTotal != total))
            {
                if (_logger.IsInfo) _logger.Info($"Blocks download        {string.Empty.PadLeft(9 - current.ToString().Length, ' ')}{current}/{total} | {(current - _firstCurrent) / (DateTime.UtcNow - _firstNotificationTime).TotalSeconds:F2}bps");
                _lastSyncNotificationTime = DateTime.UtcNow;
                _lastCurrent = current;
                _lastTotal = total;
            }

            if (isFirst)
            {
                _firstCurrent = _lastCurrent;
                _firstNotificationTime = DateTime.UtcNow;
                isFirst = false;
            }
        }

        public void ReportBlocksDownload(long current, long requested, long total, decimal? ratio = null)
        {
            // create sync stats like processing stats?
            if (DateTime.UtcNow - _lastSyncNotificationTime >= TimeSpan.FromSeconds(1)
                && (_lastCurrent != current || _lastTotal != total))
            {
                if (_logger.IsInfo) _logger.Info($"Blocks download        S:{string.Empty.PadLeft(9 - current.ToString().Length, ' ')}{current}/R:{string.Empty.PadLeft(9 - requested.ToString().Length, ' ')}{requested}/T:{total} | {(current - _firstCurrent) / (DateTime.UtcNow - _firstNotificationTime).TotalSeconds:F2}bps | {ratio:p2}");
                _lastSyncNotificationTime = DateTime.UtcNow;
                _lastCurrent = current;
                _lastTotal = total;
            }

            if (isFirst)
            {
                _firstCurrent = _lastCurrent;
                _firstNotificationTime = DateTime.UtcNow;
                isFirst = false;
            }
        }
    }
}