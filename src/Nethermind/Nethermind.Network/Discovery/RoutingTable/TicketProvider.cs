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
using System.Threading.Tasks;
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
using Nethermind.HashLib;

namespace Nethermind.Network.Discovery.RoutingTable
{
    public class TicketProvider : ITicketProvider
    {

        private readonly TimeSpan _ticketTimeBucketLen = new TimeSpan(0, 1, 0);
        private readonly int _searchForceQuery = 4;

        private readonly int _timeWindow = 10;

        private readonly int _wantTicketsInWindow = 10;

        private readonly TimeSpan _radiusTC = new TimeSpan(0, 20, 0);

        private readonly TimeSpan _keepTicketConst = new TimeSpan(0, 10, 0);

        private readonly TimeSpan _keepTicketExp = new TimeSpan(0, 5, 0);

        private readonly TimeSpan _topicQueryResend = new TimeSpan(0, 1, 0);
        private readonly TimeSpan _topicQueryTimeout = new TimeSpan(0, 0, 5);

        private readonly TimeSpan _collectFrequency = new TimeSpan(0, 0, 30);
        private readonly TimeSpan _registerFrequency = new TimeSpan(0, 0, 60);

        private readonly int _maxCollectDebt = 10;

        private readonly int _maxRegisterDebt = 5;
        // radius detector and target address generator
	    // exists for both searched and registered topics
        public Dictionary<Topic, TopicRadius> radius { get; private set; }

        // Contains buckets (for each absol                                                                                                                                                                                                                                                                               ute minute) of tickets
        // that can be used in that minute.
        // This is only set if the topic is being registered.
        public Dictionary<Topic, TopicTickets> tickets { get; private set; }
        
        private List<Topic> _regQueue = new List<Topic>();

        private List<Topic> _regSet = new List<Topic>();

        private Dictionary<Node, Ticket> _nodes = new Dictionary<Node, Ticket>();

        private Dictionary<Node, ReqInfo> _nodeLastReq = new Dictionary<Node, ReqInfo>();

        private int _lastBucketFetched;

        private TicketRef _nextTicketCached;

        private long nextTicketReg; // What's this for?

        private ConcurrentDictionary<Topic, SearchTopic> _searchTopicMap = new ConcurrentDictionary<Topic, SearchTopic>();

        private long _nextTopicQueryCleanup;

        public ConcurrentDictionary<Node, Dictionary<Keccak, SentQuery>> QueriesSent { get; set; }

        private Random RandomNumberGenerator = new System.Random();

        public TicketProvider()
        {
            //_logger.Trace($"^N %010x\n { sha }");
            QueriesSent = new ConcurrentDictionary<Node, Dictionary<Keccak, SentQuery>>();

        }

        // addTopic starts tracking a topic. If register is true,
        // the local node will register the topic and tickets will be collected.
        public void addTopic(Topic topic, bool register)
        {
           //_logger.trace($"Adding discovery topic { topic } register { register }");
           if (!radius.ContainsKey(topic)) {
               radius[topic] = new TopicRadius(topic);
           }
           if (register && tickets.ContainsKey(topic)) {
               tickets[topic] = new TopicTickets();
           }
        }

        public void addSearchTopic(Topic t, ConcurrentQueue<Node> foundChn) {
            addTopic(t, false);
            if (_searchTopicMap.ContainsKey(t) && _searchTopicMap[t].foundChn == null) { //searchTOpicMap[t]?.
                _searchTopicMap[t] = new SearchTopic(foundChn);

            }
        }

        public void removeSearchTopic(Topic t) {
            if (_searchTopicMap.ContainsKey(t) && _searchTopicMap[t].foundChn != null) {
                SearchTopic discardedValue;
                _searchTopicMap.TryRemove(t, out discardedValue);
            }
        }

        public void removeRegisterTopic(Topic topic) {
            if(tickets.ContainsKey(topic)) {
                return;
            }
            foreach (List<TicketRef> list in tickets[topic].buckets.Values) {
                foreach (TicketRef refer in list) {
                    refer.t.refCnt--;
                    if (refer.t.refCnt == 0) {
                        _nodes.Remove((Node)refer.t.node);
                        _nodeLastReq.Remove((Node)refer.t.node);
                    }
                }
            }
        }

        public Topic[] regTopicSet() {
            Topic[] topics = new Topic[tickets.Count];
            int i = 0;
            foreach (Topic topic in tickets.Keys) {
                topics[i] = topic;
                i++;
            }
            return topics;
        }

        public (LookupInfo, TimeSpan) nextRegisterLookup() {
            TimeSpan delay = new TimeSpan(0, 0, 0);
            foreach (Topic topic in tickets.Keys) {
                if (_regSet.Contains(topic)) {
                    _regQueue.Add(topic);
                    _regSet.Add(topic);
                }
            }

            while (_regQueue.Count() > 0) {
                // Fetch the next topic from the queue, and ensure it still exists
                Topic topic = _regQueue[0];
                _regQueue = (List<Topic>)_regQueue.Skip(1);
                _regSet.Remove(topic);

                if (!tickets.ContainsKey(topic)) {
                    continue;
                }
                // If the topic needs more tickets, return it
                if (tickets[topic].nextLookup < Stopwatch.GetTimestamp()) {
                    LookupInfo next = radius[topic].nextTarget(false);
                    delay = new TimeSpan(0, 0, 0, 100);
                    //_logger.Trace($"Found discovery topic to register topic {topic} \n Target {next.target} \n delay {delay}");
                    return (next, delay);
                }
                // No registration topics found or all exhausted, sleep
                delay = new TimeSpan(0, 0, 40);
                //_logger.Trace($"No topic found to register \n delay { delay} ");
                
            }
            return (new LookupInfo(), delay);
        } 

        public LookupInfo nextSearchLookup(Topic topic) {
            TopicRadius tr = radius[topic];
            LookupInfo target = tr.nextTarget(tr.radiusLookupCnt >= _searchForceQuery);
            if (target.radiusLookup) {
                tr.radiusLookupCnt++;
            } else {
                tr.radiusLookupCnt = 0;
            }
            return target;
        }

        // ticketsInWindow returns the tickets of a given topic in the registration window.
        public List<TicketRef> ticketsInWindow(Topic topic) {
	        // Sanity check that the topic still exists before operating on it
            if (!tickets.ContainsKey(topic)) {
                //_logger.Trace($"Listing non-existing discovery tickets, topic { topic }");
                return null;
            }
            List<TicketRef> ticketList = new List<TicketRef>();

            Dictionary<int, List<TicketRef>> buckets = tickets[topic].buckets;
            for (int idx = 0; idx < _timeWindow ; idx++) {
                ticketList = (List<TicketRef>)ticketList.Concat(buckets[_lastBucketFetched+idx]);
            }
            //_logger.Trace($"Retrieved discovery registration tickets, topic { topic }, from {_lastBucketFetched}, tickets {tickets.Length}");
            return ticketList;
        }

        public void removeExcessTickets(Topic t) {
            List<TicketRef> excess_tickets = ticketsInWindow(t);
            if (tickets.Count() <= _wantTicketsInWindow) {
                    return;
            }
            IEnumerable<TicketRef> ticketsToRemove = (from x in excess_tickets
                       select x).OrderBy(x => x.waitTime()).Skip(_wantTicketsInWindow);
            foreach (TicketRef tr in ticketsToRemove) {
                removeTicketRef(tr);
            }
        }

        public void addTicketRef(TicketRef r) {
            Topic topic = r.t.topics[r.idx];
            TopicTickets ticketSet = tickets[topic];
            if (tickets == null) {
                //_logger.Trace($"Adding ticket to non-existent topic { topic }");
                return;
            }
            int bucket = (int)((int)r.t.regTime[r.idx] / (int)_ticketTimeBucketLen.TotalMilliseconds);
            ticketSet.buckets[bucket].Add(r);
            r.t.refCnt++;

            long min = Stopwatch.GetTimestamp() - _collectFrequency.Ticks*_maxCollectDebt;
            if (ticketSet.nextLookup < min) {
                ticketSet.nextLookup = min;
            }
            ticketSet.nextLookup += _collectFrequency.Ticks;
        }

        public (TicketRef, TimeSpan) nextFilteredTicket() {
            long now = Stopwatch.GetTimestamp();
            while (true) {
                (TicketRef ticket, TimeSpan wait) = nextRegisterableTicket();
                if (ticket == null) {
                    return (ticket, wait);
                }
                //_logger.Trace($"Found discovery ticket to register node {ticket.t.node} serial {ticket.t.serial} wait {wait}");

                long regTime = now + wait.Ticks;
                Topic topic = ticket.t.topics[ticket.idx];
                if(tickets.ContainsKey(topic)) {
                    if(tickets.ContainsKey(topic) && regTime >= tickets[topic].nextReg) {
                        return (ticket, wait);
                    }
                }
                removeTicketRef(ticket);
            }
        }

        public void ticketRegistered(TicketRef ticketRef)
        {
            long now = Stopwatch.GetTimestamp();

            Topic topic = ticketRef.t.topics[ticketRef.idx];
            TopicTickets ticketSet = tickets[topic];
            long min = now - _registerFrequency.Ticks*_maxRegisterDebt;
            if (min > ticketSet.nextReg) {
                ticketSet.nextReg = min;
            }
            ticketSet.nextReg += _registerFrequency.Ticks;
            tickets[topic] = ticketSet;

            removeTicketRef(ticketRef);
        }

        // nextRegisterableTicket returns the next ticket that can be used
        // to register.
        //
        // If the returned wait time <= zero the ticket can be used. For a positive
        // wait time, the caller should requery the next ticket later.
        //
        // A ticket can be returned more than once with <= zero wait time in case
        // the ticket contains multiple topics.
        private (TicketRef, TimeSpan) nextRegisterableTicket() {
            long now = Stopwatch.GetTimestamp();
            if (_nextTicketCached != null) {
                return (_nextTicketCached, new TimeSpan(_nextTicketCached.topicRegTime() - now));
            }

            for (int bucket = _lastBucketFetched; ; bucket++) {
                bool empty = true;
                TicketRef nextTicket = new TicketRef(null, 0);

                foreach (TopicTickets ticketSet in tickets.Values) {
                    if (ticketSet.buckets.Count() != 0) {
                        empty = false;

                        ICollection<TicketRef> list = ticketSet.buckets[bucket];
                        foreach (TicketRef ticketRef in list) {
                            //_logger.Trace($" nrt bucket = { bucket } node = { ticketRef.t.node.ID.Bytes.Slice(0, 8) } sn = { ticketRef.topic.serial } wait = { TimeSpan(topicRegTime() - now) } ");
                            if (nextTicket.t == null || ticketRef.topicRegTime() < nextTicket.topicRegTime()) {
                                    nextTicket = ticketRef;
                            }
                        }
                        
                    }
                }
                if (empty) {
                    return (null, new TimeSpan(0));
                }
                if (nextTicket.t != null) {
                    _nextTicketCached = nextTicket; // TODO: !: Turn next ticket cached into a span
                    return (nextTicket, new TimeSpan(nextTicket.topicRegTime() - now));
                }
                _lastBucketFetched = bucket;
            }
        }

        // removeTicket removes a ticket from the ticket store
        public void removeTicketRef(TicketRef ticketRef) {
            //_logger.Trace($"Removing discovery ticket reference node {ticketRef.t.node.ID} serial {ticketRef.t.serial}");

            // Make nextRegisterableTicket return the next available ticket
            _nextTicketCached = null;

            Topic topic = ticketRef.topic();


            if (tickets.ContainsKey(topic)) {
                //_logger.Trace($"Removing tickets from unknown topic {topic}");
                return;
            }
            TopicTickets ticketList = tickets[topic];
            int bucket = (int)((int)ticketRef.t.regTime[ticketRef.idx] / (int)_ticketTimeBucketLen.Ticks);
            IEnumerable<TicketRef> list = ticketList.buckets[bucket];
            int idx = -1;
            for (int i = 0; i <= list.Count(); i++) {
                TicketRef bt = list.ElementAt(i);
                if (bt.t == ticketRef.t) {
                    idx = i;
                    break;
                }
            }
            if (idx == -1) {
                //_logger.Trace("Panic: idx = -1 in Nethermind.Network.Discovery.TicketProvider.removeTicketRef()");
            }
            list = list.Take(idx).Concat(list.Skip(idx+1));
            if (list.Count() != 0) {
                ticketList.buckets[bucket] = (List<TicketRef>)list;
            } else {
                ticketList.buckets.Remove(bucket);
            }
            ticketRef.t.refCnt--;
            if (ticketRef.t.refCnt == 0) {
                _nodes.Remove((Node)ticketRef.t.node);
                _nodeLastReq.Remove((Node)ticketRef.t.node);
            }
        }

        // delegate byte[] ping(Node node);

        public void registerLookupDone(LookupInfo lookup, List<Node> nodes, Func<Node, byte[]> ping) {
            long now = Stopwatch.GetTimestamp();
            for (int i=0; i < nodes.Count(); i++) {
                Node n = nodes[i];
                if (i==0 || (BitConverter.ToUInt64(n.Id.Bytes.Take(8).ToArray(), 0)^BitConverter.ToUInt64(lookup.target.Bytes.Take(8).ToArray(), 0)) < radius[lookup.topic]._minRadius) {
                    if (lookup.radiusLookup) {
                        if(!_nodeLastReq.ContainsKey(n) || (new TimeSpan(now - _nodeLastReq[n].time)) > _radiusTC) { // TODO: Might be able to use a monad for this
                            _nodeLastReq[n] = new ReqInfo(ping(n), lookup, now);
                        } 
                     } else {
                         if (nodes[i] == null) {
                             _nodeLastReq[n] = new ReqInfo(ping(n), lookup, now);
                         }
                     }
                }
            }
        }

        public void searchLookupDone(LookupInfo lookup, List<Node> nodes, Func<Node, Topic, byte[]> query) {
            long now = Stopwatch.GetTimestamp();
            for (int i = 0; i < nodes.Count; i++) {
                Node n = nodes[i];
                if (i==0 || (BitConverter.ToUInt64(n.Id.Bytes.Take(8).ToArray(), 0)^BitConverter.ToUInt64(lookup.target.Bytes.Take(8).ToArray(), 0)) < radius[lookup.topic]._minRadius) {
                    if (lookup.radiusLookup) {
                        if (_nodeLastReq.ContainsKey(n) && (new TimeSpan(now - _nodeLastReq[n].time)) > _radiusTC) { // TODO: Monad
                            _nodeLastReq[n] = new ReqInfo(null, lookup, now);
                        }
                    } // else {
                    if (canQueryTopic(n, lookup.topic)) {
                        Keccak hash = new Keccak(query(n, lookup.topic));
                        if (hash != null) {
                            addTopicQuery(hash, n, lookup);
                        }
                    }
                     //}
                }
            }
        }

        public void adjustWithTicket(long now, Keccak targetHash, in ITicket t) { // TODO: Check use of in
            for (int i = 0; i < t.topics.Count(); i++) {
                if(radius.ContainsKey(t.topics[i])) {
                    radius[t.topics[i]].adjustWithTicket(now, targetHash, new TicketRef(t, i));
                }
            }
        }
        
        public void addTicket(long localTime, byte[] pingHash, in Ticket ticket) {
            //_logger.Trace($"Adding discovery ticket node {ticket.node.ID} serial {ticket.serial}");

            if(!_nodeLastReq.ContainsKey((Node)ticket.node)) {
                if (pingHash == _nodeLastReq[(Node)ticket.node].pingHash) {
                    return;
                }
            }
            adjustWithTicket(localTime, _nodeLastReq[(Node)ticket.node].lookup.target, ticket);

            if (_nodeLastReq[(Node)ticket.node].lookup.radiusLookup || (_nodes.ContainsKey((Node)ticket.node) && _nodes[(Node)ticket.node] != null)) {
                return;
            }

            Topic topic = _nodeLastReq[(Node)ticket.node].lookup.topic;
            int topicIdx = ticket.findIdx(topic);
            if (topicIdx == -1) {
                return;
            }

            Int32 bucket = (int)((int)localTime / (int)_ticketTimeBucketLen.Ticks);
            if (_lastBucketFetched == 0 || bucket < _lastBucketFetched) {
                _lastBucketFetched = bucket;
            }

            if (tickets.ContainsKey(topic)) {
                TimeSpan wait = new TimeSpan(ticket.regTime[topicIdx] - localTime);
                double rnd = RandomNumberGenerator.NextDouble();
                if (rnd > 10) {
                    rnd = 10;
                }
                if ( wait.Ticks < (_keepTicketConst.Ticks + _keepTicketExp.Ticks*rnd) ) {
                    // use the ticket to register this topic
			        //fmt.Println("addTicket", ticket.node.ID[:8], ticket.node.addr().String(), ticket.serial, ticket.pong)
                    addTicketRef(new TicketRef(ticket, topicIdx));
                }
            }
            if (ticket.refCnt > 0) {
                _nextTicketCached = null;
                _nodes[(Node)ticket.node] = ticket;
            }
        }

        public Ticket getNodeTicket(Node node) {
            if (!_nodes.ContainsKey(node) || _nodes[node] == null) {
                //_logger.Trace($"Retreiving node ticket, node {node.ID} serial { null} ");
            } else {
                //_logger.Trace($"Retreiving ndoe ticket, node {node.ID} serial {_nodes[node].serial}");
            }
            return _nodes[node];
        }

        public bool canQueryTopic(Node node, Topic topic) {
            if (QueriesSent.ContainsKey(node)) {
                
            
                Dictionary<Keccak, SentQuery> qq = QueriesSent[node];
                if (qq != null) {
                    long now = Stopwatch.GetTimestamp();
                    foreach (KeyValuePair<Keccak, SentQuery> entry in qq) {
                        SentQuery sq = entry.Value;
                        if (sq.lookup.topic == topic && (new TimeSpan(sq.sent)) > (new TimeSpan(now - _topicQueryResend.Ticks))) {
                            return false;
                        }
                    }
                }
            }
            return true;            
        }

        // Called by searchLookupDone(...)
        public void addTopicQuery(Keccak hash, Node node, LookupInfo lookup) {
            long now = Stopwatch.GetTimestamp();
            if (!QueriesSent.ContainsKey(node)) {
                QueriesSent[node] = null;
            }
            Dictionary<Keccak, SentQuery> qq = QueriesSent[node];
            if (qq == null) {
                qq = new Dictionary<Keccak, SentQuery>();
                QueriesSent[node] = qq;
            }
            qq[hash] = new SentQuery(now, lookup);
            cleanupTopicQueries(now);
        }

        //
        public void cleanupTopicQueries(long now) {
            if (_nextTopicQueryCleanup > now) {
                return;
            }
            TimeSpan exp = new TimeSpan(now - _topicQueryResend.Ticks);
            for (int i = 0; i < QueriesSent.Count; i++) {
                KeyValuePair<Node, Dictionary<Keccak, SentQuery>> entry = QueriesSent.ElementAt(i);
                Node n = entry.Key;
                Dictionary<Keccak, SentQuery> qq = entry.Value;
                for (int j = 0; j < qq.Count; j++) {
                    KeyValuePair<Keccak, SentQuery> qqEntry = qq.ElementAt(j);
                    Keccak h = qqEntry.Key;
                    SentQuery q = qqEntry.Value;
                    if (q.sent < exp.Ticks) {
                        qq.Remove(h); // TODO: IMPORTANT: Revisit this to change the index

                    }
                }
                if (qq.Count == 0) {
                    Dictionary<Keccak, SentQuery> dump;
                    QueriesSent.TryRemove(n, out dump);
                }
            }
            _nextTopicQueryCleanup = now + _topicQueryTimeout.Ticks;
        }

        //TODO: merge this class's RpcNode with Nethermind.Stats.Model.Node with discoveryNode = True or IPEndpoint;reg
        public async Task gotTopicNodes(Node from, Keccak hash, ICollection<Node> nodes) {
            long now = Stopwatch.GetTimestamp();
            //_logger.Trace($"got {from.Address.ToString()} {hash} {nodes.Count}");
            Dictionary<Keccak, SentQuery> qq = QueriesSent[from];

            SentQuery q = qq[hash];
            if ((new TimeSpan(now)) > (new TimeSpan(q.sent + _topicQueryTimeout.Ticks))) {
                //_logger.Trace("Passed topicquerytimeout");
                return;
            }
            double inside = 0;
            if (nodes.Count > 0) {
                inside = 1;
            }
            radius[q.lookup.topic].adjust(now, q.lookup.target, from.IdHash, inside);
            if (_searchTopicMap.ContainsKey(q.lookup.topic)) {
                ConcurrentQueue<Node> chn = _searchTopicMap[q.lookup.topic].foundChn;
            foreach (Node n in nodes) {
                   chn.Enqueue(n);
            }
            
            
            //WritableChannel<Node> chn = searchTopicMap[q.lookup.topic].foundChn;
            
            //TODO: figure this part out... which timer starts a routine that is reading this channel? 
            //while(await chn.WaitForWriteAsync()) {
            //    if(!(chn?.TryWrite(n))) {
            //        return;
            //    }
            }
        }

        public struct SearchTopic
        {
            public ConcurrentQueue<Node> foundChn;

            public SearchTopic(ConcurrentQueue<Node> _foundChn) {
                foundChn = _foundChn;
            }   
        }

        public void Initialize()
        {
        }
    }
}