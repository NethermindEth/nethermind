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
using Nethermind.Network;

namespace Nethermind.Network.Discovery.RoutingTable
{
    public class TopicTable : ITopicTable
    {
        private NetworkStorage _db;
        private Node MasterNode { get; private set;}
        private  Dictionary<Node, NodeInfo> _nodes;

        private Dictionary<Topic, TopicInfo> _topics;
        private ulong _globalEntries = 0;
        private TopicRequestQueue _requested;

        private ulong _requestCnt = 0;

        private long _lastGarbageCollection;

        private readonly TimeSpan _gcInterval = new TimeSpan(0, 0, 1);

        private readonly TimeSpan _minWaitPeriod = new TimeSpan(0, 1, 0);

        private readonly int _maxEntries = 10000;

        private readonly int _maxEntriesPerTopic = 50;

        private readonly TimeSpan _fallbackRegistrationExpiry = new TimeSpan(1, 0, 0);

        private readonly TimeSpan _regTimeWindow = new TimeSpan(0, 0, 10);

        private Stopwatch _timestamp = new Stopwatch();

        private readonly bool _printTestImgLogs = true;

        public TopicTable(INetworkStorage nodeDb, Node self)
        {
            _logger.Trace($"^N %010x\n { sha }");
            _db = nodeDb;
        }
        public TopicInfo getOrNewTopic(Topic topic)
        {
            if (_logger.IsTrace) _logger.Trace($"Adding node to NodeTable: {node}");
            TopicInfo ti = _topics[topic];
            if (this == null) { 
                TopicRequestQueueItem rqItem = new TopicRequestQueueItem(topic, _requestCnt);
                TopicInfo ti = new TopicInfo(rqItem);
            }
            _topics[topic] = ti;
            _requested.Push(rqItem);
            return ti;
        }

        public bool checkDeleteTopic(Topic topic)
        {
           TopicInfo ti = _topics[topic];
           if (ti == null) { 
               return;
           }
           if (ti.entries.Length == 0 && ti.wcl.hasMinimumWaitPeriod()) {
               _topics.Remove(topic);
               return true;
               _requested.Remove(ti.rqItem.index);
           }
        } 

        public bool checkDeleteNode(Node node) {
            if (_nodes.ContainsKey(node)) {
                Node n = _nodes[node];
                if (n.entries.Count == 0 && n.noRegUntil < _timestamp.GetTimestamp()) {
                    _nodes.Remove(node);
                    return true;
                }
            }
        }

        // TODO: storeTicketCounters
        public void storeTicketCounters(Node node)
        {
             _logger.Trace("Not Implemented: storeTicketCounteres");
        }

        public NodeInfo getOrNewNode(Node node) // used by storeTicketCounters
        {
            NodeInfo n = _nodes[node];
            if ( n == null ) {
                int issued, used;
                if (_db != null ){
                    //TODO:
                    //Ticket[] issued = _db.fetchTopicRegTicketsIssued(node.ID); // check if node.ID is nodID, hceck
                    //Ticket[] used = _db.fetchTopicRegTicketsUsed(node.ID);
                }
                NodeInfo n = new NodeInfo(issued, used); // possibly sending uninitialized values?
                return n;
            }
        }

        private void collectGarbage() {
            long tm = _timestamp.GetTimestamp();
            TimeSpan sinceLastGarbageCollection = new TimeSpan (tm - _lastGarbageCollection);
            if (sinceLastGarbageCollection < _gcInterval) {
                return;
            }
            _lastGarbageCollection = tm;

            for (int i = 0; i < _nodes.Count; i++) {
                NodeInfo n = _nodes.ElementAt(i);
                for (int j = 0; j < n.entries.Count; j++) {
                    TopicEntry e = n.entries.ElementAt(j);
                    if (e.expire <= tm) {
                        deleteEntry(e);
                    }
                }
                bool deleted = checkDeleteNode(node);
                if(deleted) {
                    i--;
                }
            }

            for (int i = 0; i < _topics.Count; i++) {
                bool deleted = checkDeleteTopic(_topics[i]);
                if (deleted) {
                    i--;
                }
            }
        }

        public Ticket getTicket(Node node, ICollection<Topic> topics) {
            collectGarbage();

            long now = _timestamp.GetTimestamp();
            NodeInfo n = getOrNewNode(node); //TODO: Convert to NodeLifecycleManager
            n.lastIssuedTicket++; //TODO: Convert to NodeStats Model + NodeLifecycleManager
            storeTicketCounts(node);

            Ticket tic = new Ticket(now, topics, n.lastIssuedTicket, new List<long>());
            for (int i = 0; i < topics.Count; i++) {
                Topic topic = topics[i];
                TimeSpan waitPeriod;
                if (_topics.ContainsKey(topic)) {
                    if (topics[topic] != null) {
                        waitPeriod = topic.wcl.waitPeriod;
                    } else {
                        waitPeriod = _minWaitPeriod;
                    }
                }

                tic.regTime[i] = now + waitPeriod.Ticks;
            }
            return tic;
        }
        
        public ICollection<Node> getEntries(Topic topic) {
            collectGarbage();
            if (!_topics.containsKey(topic)) {
                return null;
            }
            TopicInfo te = _topics[topic];
            
            List<Node> nodes = new List<LinkedListNode>(te.entries.Count());
            
            for (int i = 0; i < te.entries.Count(); i++) {
                e = te.entries.Values.ElementAt(i);
                nodes[i] = e.node;
            }
            _requestCnt++;
            _requested.Update(te.rqItem, _requestCnt);
            return nodes;
        }

        public void addEntry(Node node, Topic topic) {
            NodeInfo n = getOrNewNode(node);

            //clear previous entries by the same node
            for ( int i = 0; i < n.entries.Count; i++) {
                n.entries.Remove(n.entries.ElementAt(i).Key);
            }

            NodeInfo n = getOrNewNode(node);

            long tm = _timestamp.GetTimestamp();
            TopicInfo te = getOrNewTopic(topic);

            if (te.entries.Count == _maxEntriesPerTopic) {
                deleteEntry(te.getFifoTail());
            }

            if (_globalEntries == _maxEntries) {
                deleteEntry(leastRequested());
            }

            long fifoIdx = te.fifoHead;
            te.fifoHead++;
            TopicEntry entry = new TopicEntry(topic, fifoIdx, node, tm + _fallbackRegistrationExpiry.Ticks);

            if (_printTestImgLogs) {
                _logger.Trace($"+ {Math.Round(tm/100000)} {topic} {t.self.sha.Bytes.Slice(0, 8)} {node.sha.Bytes.Slice(0,8)} ");
            }
            te.entries[fifoIdx] = entry;
            n.entries[topic] = entry;
            _globalEntries++;
            te.wcl.registered(tm);
        }
        public TopicEntry leastRequested() {
            // Removes non existent topics
            //TODO: Check this, should it be null or ContainsKey
            while (_requested.Count > 0 && _topics[_requested.ElementAt(0).topic] == null) {
                _requested.Pop(); // TODO: dotnet core heap implementation
            }
            // CHecks to make sure there are still topics that exist
            if (_requested.Count == 0) {
                return null;
            }
            return _topics[_requested.ElementAt(0).topic].getFifoTail();
        }

        public void deleteEntry(TopicEntry e) {
            if (_printTestImgLogs) {
                _logger.Trace($"*- {(new TimeSpan(_timestamp.GetTimestamp())).TotalMilliseconds} {e.topic} {self.sha.Bytes.Slice(0,8)} {e.node.sha.Bytes.Slice(0,8)}");
            }
            Dictionary<Topic, TopicEntry> ne = _nodes[e.node].entries;
            ne.Remove(e.topic);
            if (ne.Count == 0) {
                checkDeleteNode(e.node);
            }
            Dictionary<UInt64, TopicEntry> te = _topics[e.topic];
            te.Remove(e.fifoIdx);
            if(te.entries.Count == 0) {
                checkDeleteTopic(e.topic);
            }
            _globalEntries--;
        }

        //TODO: replace with WaitControlLoop's no RegTimeout(), or make the call shared between the two
        public TimeSpan noRegTimeout() {
            double e = nextExpDouble();
            if ( e > 100 ) {
                e = 100;
            }
            return SECOND * avgnoRegTimeout * e;
        }


        public bool useTicket(Node node, uint serialNo, ICollection<Topic> topics, int idx, long issueTime, ICollection<TimeSpan> waitPeriods) {
            _logger.Trace($"Using discovery ticket serial { serialNo } topics {topics} waits {waitPeriods}");
            collectGarbage();

            NodeInfo n = getOrNewNode(node);

            if (serialNo < n.lastUsedTicket) {
                return false;
            }

            long tm = _timestamp.GetTimestamp();

            if (serialNo > n.lastUsedTicket && tm < n.noRegUntil) {
                return false;
            }
            if (serialNo != n.lastUsedTicket) {
                n.lastUsedTicket = serialNo;
                n.noRegUntil = tm + noRegTimeout().Ticks;
                storeTicketCounters(node);
            }

            TimeSpan currTime = new TimeSpan(tm);
            TimeSpan regTime = new TimeSpan(issueTime + waitPeriods[idx].Ticks);
            TimeSpan relTime = currTime - regTime;

            if (relTime.TotalMilliseconds*1000000 >= -1  // Turn into nanoseconds
                && relTime <= new TimeSpan(0, 0, (_regTimeWindow.TotalMilliseconds+1)/1000)
            ) {
                if ( !n.entries.ContainsKey(topics[idx]) ) {
                    addEntry(node, topics[idx]); 
                }
                TopicInfo e = n.entries.topics[idx];
                if ( e == null ) {
                    addEntry(node, topics[idx]);
                } else {
                    e.expire = tm + _fallbackRegistrationExpiry.Ticks;
                }
                return true;
            }

            return false;
        }

        public void Initialize()
        {
        }

        

    }
}