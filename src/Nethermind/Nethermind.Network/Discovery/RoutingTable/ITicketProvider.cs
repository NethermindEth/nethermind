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
using Nethermind.Network;
using Nethermind.HashLib;

namespace Nethermind.Network.Discovery.RoutingTable
{
    public interface ITicketProvider
    {

        public Dictionary<Topic, TopicRadius> radius { get; private set; }

        // Contains buckets (for each absolute minute) of tickets
        // that can be used in that minute.
        // This is only set if the topic is being registered.
        private Dictionary<Topic, TopicTickets> tickets { get; private set; }
        
        public TicketProvider(INetworkStorage nodeDb, Node self);
        // addTopic starts tracking a topic. If register is true,
        // the local node will register the topic and tickets will be collected.
        public void addTopic(Topic topic, bool register);
        public void addSearchTopic(Topic t, WritableChannel<Node> foundChn);
        public void removeSearchTopic(Topic t);
        public void removeRegisterTopic(Topic topic);
        public Topic[] regTopicSet();
        public (LookupInfo, TimeSpan) nextRegisterLookup();

        public LookupInfo nextSearchLookup(Topic topic);
        // ticketsInWindow returns the tickets of a given topic in the registration window.
        public ICollection<TicketRef> ticketsInWindow(Topic topic);
        public void removeExcessTickets(Topic t);
        public void addTicketRef(TicketRef r);
        public (TicketRef, TimeSpan) nextFilteredTicket();

        // removeTicket removes a ticket from the ticket store
        public void removeTicketRef(TicketRef ticketRef);

        // delegate byte[] ping(Node node);

        public void registerLookupDone(LookupInfo lookup, ICollection<Node> nodes, Func<Node, byte[]> ping);
        public void searchLookupDone(LookupInfo lookup, ICollection<Node> nodes, Func<Node, Topic, byte[]> query);
        public void adjustWithTicket(long now, Keccak targetHash, ref ITicket t);
        public void addTicket(long localTime, byte[] pingHash, ref Ticket ticket);
        public bool canQueryTopic(Node node, Topic topic);
        // Called by searchLookupDone(...)
        public void addTopicQuery(Keccak hash, Node node, LookupInfo lookup);
        //
        public void cleanupTopicQueries(long now);
        //TODO: merge this class's RpcNode with Nethermind.Stats.Model.Node with discoveryNode = True or IPEndpoint;
        public async void getTopicNodes(Node from, Keccak hash, ICollection<RpcNode> nodes);
        public struct SearchTopic
        {
            public WritableChannel foundChn;

            public SearchTopic(WritableChannel<Node> _foundChn);
        }

        public void Initialize(NodeId masterNodeKey = null)
        {
            
        }
    }
}