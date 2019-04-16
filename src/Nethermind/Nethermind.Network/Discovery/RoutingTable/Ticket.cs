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

namespace Nethermind.Network.Discovery.RoutingTable
{
    public class Ticket : ITicket
    {
        public List<Topic> topics { get; private set; }
        public List<long> regTime { get; set; } // Per-topic local absolute time when the ticet can be used.

        public int serial { get; private set; } //The serial number that was issued by the server
        public long issueTime { get; private set; } // // Used by registrar, tracks absolute time when the ticket was created.

        // Fields used only by registrants
        public Node node { get; private set; }

        public int refCnt { get; set; } // tracks number of topics that will be registered using this ticket

        public byte[] pong { get; private set; } // encoded pong packet signed by the registrar

        public Ticket(long _issueTime, List<Topic> _topics, int _serial, List<long> _regTime)
        {
            //_logger.Trace($"New Ticket issue time { _issueTime } topics {_topics.Count } ");

            issueTime = _issueTime;
            topics = _topics;
            serial = _serial;
            regTime = _regTime;
        }

        public Ticket(long _issueTime, List<Topic> _topics, int _serial) {
            //_logger.Trace($"New Ticket issue time { _issueTime } topics {_topics.Count } ");

            issueTime = _issueTime;
            topics = _topics;
            serial = _serial;
            regTime = new List<long>();
        }

        public int findIdx(Topic topic) {
            return topics.IndexOf(topic);
        }
    }
}