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
        Node MasterNode { get; private set; }

        TopicInfo getOrNewTopic(Topic topic);
        bool checkDeleteTopic(Topic topic);

        bool checkDeleteNode(Node node);

        // TODO: storeTicketCounters
        NodeInfo getOrNewNode(Node node);
        Ticket getTicket(Node node, ICollection<Topic> topics);
        
        ICollection<Node> getEntries(Topic topic);
        void addEntry(Node node, Topic topic);
        TopicEntry leastRequested();

        void deleteEntry(TopicEntry e);

        //TODO: replace with WaitControlLoop's no RegTimeout(), or make the call shared between the two
        TimeSpan noRegTimeout();

        bool useTicket(Node node, UInt32 serialNo, ICollection<Topic> topics, int idx, long issueTime, ICollection<TimeSpan> waitPeriods);

        void Initialize();

    }
}