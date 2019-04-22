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
        private INetworkConfig _networkConfig;
        public Node MasterNode { get; private set;}
        private  Dictionary<Node, NodeInfo> _nodes;

        private Dictionary<Topic, TopicInfo> _topics;
        private ulong _globalEntries = 0;
        private TopicRequestQueue _requested = new TopicRequestQueue();

        private long _requestCnt = 0;

        private long _lastGarbageCollection;

        private readonly TimeSpan _gcInterval = new TimeSpan(0, 0, 1);

        private readonly TimeSpan _minWaitPeriod = new TimeSpan(0, 1, 0);

        private readonly int _maxEntries = 10000;

        private readonly int _maxEntriesPerTopic = 50;

        private readonly TimeSpan _fallbackRegistrationExpiry = new TimeSpan(1, 0, 0);

        private readonly TimeSpan _regTimeWindow = new TimeSpan(0, 0, 10);

        private Stopwatch _timestamp = new Stopwatch();

        private readonly bool _printTestImgLogs = true;

        public TopicTable(INetworkConfig networkConfig)
        {
            //_logger.Trace($"^N %010x\n { self.Id }");
            _networkConfig = networkConfig;
        }
        public TopicInfo getOrNewTopic(Topic topic)
        {
            //if (_logger.IsTrace) _logger.Trace($"Adding node to NodeTable: {node}");
            if (!_topics.ContainsKey(topic)) { 
                TopicRequestQueueItem rqItem = new TopicRequestQueueItem(topic, (ulong)_requestCnt, 0);
                TopicInfo ti = new TopicInfo(rqItem);
            
                _topics[topic] = ti;
                _requested.Push(rqItem);
            }
            return _topics[topic];
        }

        public bool checkDeleteTopic(Topic topic)
        {
           if (!_topics.ContainsKey(topic)) {
               return false;
           }
           TopicInfo ti = _topics[topic];
           if (ti.entries.Count() == 0 && ti.wcl.hasMinimumWaitPeriod()) {
               _topics.Remove(topic);
               _requested.Remove(ti.rqItem.index);
               return true;
           }
           return false;
        } 

        public bool checkDeleteNode(Node node) {
            if (_nodes.ContainsKey(node)) {
                NodeInfo n = _nodes[node];
                if (n.entries.Count == 0 && n.noRegUntil < (ulong)Stopwatch.GetTimestamp()) {
                    _nodes.Remove(node);
                    return true;
                }
                return false;
            }
            return false;
        }

        // TODO: storeTicketCounters
        public void storeTicketCounters(Node node)
        {
             //_logger.Trace("Not Implemented: storeTicketCounteres");
        }

        public NodeInfo getOrNewNode(Node node) // used by storeTicketCounters
        {
            int issued, used;

            NodeInfo n = _nodes[node];
            if (!_nodes.ContainsKey(node)) {
                //if (_db != null ){
                    //TODO:
                    // NOT IMPLEMENTED
                    //Ticket[] issued = _db.fetchTopicRegTicketsIssued(node.ID); // check if node.ID is nodID, hceck
                    //Ticket[] used = _db.fetchTopicRegTicketsUsed(node.ID);
                    //n = new NodeInfo(issued, used); // possibly sending uninitialized values?
                //}
                return n;
            } else {
                return n;
            }
        }

        private void collectGarbage() {
            long tm = Stopwatch.GetTimestamp();
            TimeSpan sinceLastGarbageCollection = new TimeSpan (tm - _lastGarbageCollection);
            if (sinceLastGarbageCollection < _gcInterval) {
                return;
            }
            _lastGarbageCollection = tm;

            for (int i = 0; i < _nodes.Count; i++) {
                NodeInfo n = _nodes.Values.ElementAt(i);
                for (int j = 0; j < n.entries.Count; j++) {
                    TopicEntry e = n.entries.Values.ElementAt(j);
                    if (e.expire <= tm) {
                        deleteEntry(e);
                    }
                }
                bool deleted = checkDeleteNode(_nodes.Keys.ElementAt(i));
                if(deleted) {
                    i--;
                }
            }

            for (int i = 0; i < _topics.Count(); i++) {
                bool deleted = checkDeleteTopic(_topics.Keys.ElementAt(i));
                if (deleted) {
                    i--;
                }
            }
        }

        public Ticket getTicket(Node node, List<Topic> topics) {
            collectGarbage();

            long now = Stopwatch.GetTimestamp();
            NodeInfo n = getOrNewNode(node); //TODO: Convert to NodeLifecycleManager
            n.lastIssuedTicket++; //TODO: Convert to NodeStats Model + NodeLifecycleManager
            //storeTicketCounts(node);

            Ticket tic = new Ticket(now, topics, n.lastIssuedTicket);
            for (int i = 0; i < topics.Count; i++) {
                Topic topic = topics[i];
                TimeSpan waitPeriod;
                if (_topics.ContainsKey(topic)) {
                    waitPeriod = _topics[topic].wcl.WaitPeriod;
                } else {
                    waitPeriod = _minWaitPeriod;
                }

                tic.regTime[i] = now + waitPeriod.Ticks;
            }
            return tic;
        }
        
        public List<Node> getEntries(Topic topic) {
            collectGarbage();
            if (!_topics.ContainsKey(topic)) {
                return null;
            }
            TopicInfo te = _topics[topic];
            
            List<Node> nodes = new List<Node>(te.entries.Count());
            
            for (int i = 0; i < te.entries.Count(); i++) {
                TopicEntry e = te.entries.Values.ElementAt(i);
                nodes[i] = e.node;
            }
            _requestCnt++;
            _requested.Update(te.rqItem, (ulong)_requestCnt);
            return nodes;
        }

        public void addEntry(Node node, Topic topic) {
            NodeInfo n = getOrNewNode(node);

            //clear previous entries by the same node
            for ( int i = 0; i < n.entries.Count; i++) {
                n.entries.Remove(n.entries.ElementAt(i).Key);
            }

            n = getOrNewNode(node);

            long tm = Stopwatch.GetTimestamp();
            TopicInfo te = getOrNewTopic(topic);

            if (te.entries.Count() == _maxEntriesPerTopic) {
                deleteEntry(te.getFifoTail());
            }

            if (_globalEntries == (ulong)_maxEntries) {
                deleteEntry(leastRequested());
            }

            int fifoIdx = te.fifoHead;
            te.fifoHead++;
            TopicEntry entry = new TopicEntry(topic, fifoIdx, node, tm + _fallbackRegistrationExpiry.Ticks);

            if (_printTestImgLogs) {
                //_logger.Trace($"+ {Math.Round(tm/100000)} {topic} {t.self.sha.Bytes.Slice(0, 8)} {node.sha.Bytes.Slice(0,8)} ");
            }
            te.entries[fifoIdx] = entry;
            n.entries[topic] = entry;
            _globalEntries++;
            te.wcl.registered(tm);
        }
        public TopicEntry leastRequested() {
            // Removes non existent topics
            //TODO: Check this, should it be null or ContainsKey
            while (_requested.Len() > 0 && _topics.ContainsKey(_requested.ElementAt(0).topic)) {
                _requested.Pop(); // TODO: dotnet core heap implementation
            }
            // CHecks to make sure there are still topics that exist
            if (_requested.Len() == 0) {
                return new TopicEntry();
            }
            return _topics[_requested.ElementAt(0).topic].getFifoTail();
        }

        public void deleteEntry(TopicEntry e) {
            if (_printTestImgLogs) {
               // _logger.Trace($"*- {(new TimeSpan(Stopwatch.GetTimestamp())).TotalMilliseconds} {e.topic} {self.sha.Bytes.Slice(0,8)} {e.node.sha.Bytes.Slice(0,8)}");
            }
            Dictionary<Topic, TopicEntry> ne = _nodes[e.node].entries;
            ne.Remove(e.topic);
            if (ne.Count == 0) {
                checkDeleteNode(e.node);
            }
            TopicInfo te = _topics[e.topic];
            te.entries.Remove(e.fifoIdx);
            if(te.entries.Count == 0) {
                checkDeleteTopic(e.topic);
            }
            _globalEntries--;
        }

        //TODO: replace with a static WaitControlLoop's noRegTimeout(), or make the call shared between the two
        public TimeSpan noRegTimeout() {
            WaitControlLoop wcl = new WaitControlLoop();
            return wcl.noRegTimeout();
        }


        public bool useTicket(Node node, uint serialNo, ICollection<Topic> topics, int idx, long issueTime, ICollection<uint> waitPeriods) {
            //_logger.Trace($"Using discovery ticket serial { serialNo } topics {topics} waits {waitPeriods}");
            collectGarbage();

            NodeInfo n = getOrNewNode(node);

            if (serialNo < n.lastUsedTicket) {
                return false;
            }

            long tm = Stopwatch.GetTimestamp();

            if (serialNo > n.lastUsedTicket && (ulong)tm < n.noRegUntil) {
                return false;
            }
            if (serialNo != n.lastUsedTicket) {
                n.lastUsedTicket = (int)serialNo;
                n.noRegUntil = (ulong)tm + (ulong)noRegTimeout().Ticks;
                storeTicketCounters(node);
            }

            TimeSpan currTime = new TimeSpan(tm);
            TimeSpan regTime = new TimeSpan(issueTime + waitPeriods.ElementAt(idx));
            TimeSpan relTime = currTime - regTime;

            if (relTime.TotalMilliseconds*1000000 >= -1  // Turn into nanoseconds
                && relTime <= new TimeSpan(0, 0, (int)(_regTimeWindow.TotalMilliseconds+1)/1000) // Turn Milliseconds back into Seconds for TimeSpan constructor
            ) {
                if ( !n.entries.ContainsKey(topics.ElementAt(idx)) ) {
                    addEntry(node, topics.ElementAt(idx)); 
                }
                TopicEntry e = n.entries[topics.ElementAt(idx)];
                e.expire = tm + _fallbackRegistrationExpiry.Ticks;
                return true;
            }

            return false;
        }

        public void Initialize(PublicKey masterNodeKey)
        {
            MasterNode = new Node(masterNodeKey, _networkConfig.MasterHost, _networkConfig.MasterPort);
        }

        

    }
}