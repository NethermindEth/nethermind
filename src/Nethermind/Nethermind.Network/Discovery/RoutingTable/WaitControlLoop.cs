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
using System.Linq;
using System.Security;
using System.Diagnostics;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.KeyStore;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.RoutingTable
{
    public class WaitControlLoop : IWaitControlLoop
    {
        private readonly TimeSpan minWaitPeriod = new TimeSpan(0, 1, 0);
        
        private readonly TimeSpan regTimeWindow  = new TimeSpan(0, 0, 10); // seconds
        
        
        private readonly TimeSpan avgnoRegTimeout = new TimeSpan(0, 10, 0);
        // target average interval between two incoming ad requests
        private readonly TimeSpan _wcTargetRegInterval;
        //
        private readonly TimeSpan _wcTimeConst = new TimeSpan(0, 10, 0);

        private long _lastIncoming;

        private TimeSpan _waitPeriod;

        private readonly Random RandomNumberGenerator = new Random();

        private readonly ITopicTable _topicTable;

        public WaitControlLoop(ITopicTable topicTable)
        {
            _topicTable = topicTable;
            _wcTargetRegInterval = new TimeSpan(0, 10, 0) / maxEntriesPerTopic;
        }
        public void registered(long time) {
            _waitPeriod = nextWaitPeriod(time);
            _lastIncoming = time;
        }

        private TimeSpan nextWaitPeriod(long time) {
            TimeSpan period = new TimeSpan(time - _lastIncoming);
            long frequency = Stopwatch.Frequency;
            long nanosecPerTick = (1000L*1000L*1000L) / frequency;
            // The Go mclock library measures in nanoseconds, so keep the calculations the same
            TimeSpan wp = _waitPeriod * (new TimeSpan(math.Exp(new TimeSpan((_wcTargetRegInterval.Ticks-period.Ticks)/_wcTimeConst.Ticks).TotalMilliseconds*1000000)*nanosecPerTick));

            if (wp < minWaitPeriod) {
                wp = minWaitPeriod;
            }
            return wp;
        }

        public bool hasMinimumWaitPeriod() {
            return nextWaitPeriod(Stopwatch.GetTimestamp()) == minWaitPeriod;
        }
        
        public TimeSpan noRegTimeout() {
            double e = nextExpDouble();
            if ( e > 100 ) {
                e = 100;
            }
            return SECOND * avgnoRegTimeout * e;
        }

        private double nextExpDouble() {
            double random_number = RandomNumberGenerator.NextDouble();
            return log(1-random_number)/-1;
        }
    }
}