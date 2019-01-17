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
    public class TopicRequestQueue : ITopicRequestQueue
    {
        private List<TopicRequestQueueItem> _topicRequestQueueList; // Nethermind.Core.Timestamp->DateTime or TimeSpan or DateTime?

        public TopicRequestQueue()
        {
             _topicRequestQueueList = new List<TopicRequestQueItem>();
        }
        public int Len() {
            return _topicRequestQueueList.Count;
        }

        public 

        public bool Less(int i, int j) {
            return _topicRequestQueueList[i].priority < _topicRequestQueueList[j].priority; 
            // TODO: turn this into the IComparable <T> interface for T=TopicRequestQueueItem 
        }

        public void Swap(int i, int j) {
            TopicRequestQueueItem _oldI = topicRequestQueueList[i];
            _topicRequestQueueList[i] = _topicRequestQueueList[j];
            _topicRequestQueueList[j] = _oldI;

            _topicRequestQueueList[i].index = j;
            _topicRequestQueueList[j].index = i;
        }

        public void Push(ITopicRequestQueueItem item) {
            item.index = _topicReqeustQueueList.Length;
            _topicRequestQueueList.Append(item);
        }

        public TopicRequestQueueItem Pop() {
            TopicRequestQueue old = _topicRequestQueueList;
            int n = old.len();
            TopicRequestQueueItem item = old[n-1];
            item.index = -1;
            _topicRequestQueueList = old.GetRange(0, n-1);
            return item;
        }

        public void Update(TopicRequestQueueItem item, long priority) {
            // Here is why the items store their index. Perhaps we should look up by key Topic? 
            item.priority = priority;
            _topicRequestQueueList[item.index] = item;
        }

        public void Remove(int idx)
        {
             TopicRequestQueue old = _topicRequestQueueList;
             for (int i = idx+1; i < old.Len(); i++) {
                 _topicRequestQueueList[i].index = i-1;
             }
             _topicRequestQueueList.RemoveAt(idx);
        }

        public void ElementAt(int idx)
        {
            return _topicRequestQueueList[idx];
        }
    }
}