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
    public interface ITopicTable
    {
        private Node MasterNode { get; private set;}

        public TopicTable(INetworkStorage nodeDb, Node self);
        public TopicInfo getOrNewTopic(Topic topic);
        public bool checkDeleteTopic(Topic topic);

        public bool checkDeleteNode(Node node);

        // TODO: storeTicketCounters
        public NodeInfo getOrNewNode(Node node);
        public Ticket getTicket(Node node, ICollection<Topic> topics);
        
        public ICollection<Node> getEntries(Topic topic);
        public void addEntry(Node node, Topic topic);
        public TopicEntry leastRequested();

        public void deleteEntry(TopicEntry e);

        //TODO: replace with WaitControlLoop's no RegTimeout(), or make the call shared between the two
        public TimeSpan noRegTimeout();

        public bool useTicket(Node node, UInt32 serialNo, ICollection<Topic> topics, int idx, UInt64 issueTime, ICollection<TimeSpan> waitPeriods);

        public void Initialize();

        

    }
}