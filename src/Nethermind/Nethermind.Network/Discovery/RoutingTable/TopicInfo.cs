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
    public class TopicInfo: ITopicInfo
    {
        public Dictionary<int, TopicEntry> entries { get; } 

        public int fifoHead { get; set; }
            
        public int fifoTail { get; set; }

        public TopicRequestQueueItem rqItem { get; set;  }

        public WaitControlLoop wcl { get; private set; }

        public TopicInfo(TopicRequestQueueItem requestItem)
        {
            entries = new Dictionary<int, TopicEntry>();
            fifoHead = 0; fifoTail = 0;
            rqItem = requestItem;
            wcl = new WaitControlLoop();
        }

        public TopicEntry getFifoTail() {
            var lastItemIdx = entries.Keys.Max();
            TopicEntry lastItem = entries[lastItemIdx];
            entries.Remove(lastItemIdx);
            fifoTail = entries.Keys.Max();
            return lastItem;
        }
    }
}