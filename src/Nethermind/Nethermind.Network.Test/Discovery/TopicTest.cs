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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Json;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [TestFixture]
    public class TopicTests
    {
        private Signature[] _signatureMocks;
        private PublicKey[] _nodeIds;
        private Dictionary<string, PublicKey> _signatureToNodeId;

        private IDiscoveryManager _discoveryManager;
        private IMessageSender _udpClient;
        private INodeTable _nodeTable;
        private IConfigProvider _configurationProvider;
        private ITimestamp _timestamp;


       private delegate TimeSpan waitFn(Keccak addr);

        [SetUp]
        public void Initialize()
        {
            
        }

        [Test]
        public void TopicRadiusTest()
        {
            long now = Stopwatch.GetTimestamp();
            Topic topic = new Topic("qwerty");
            TopicRadius rad = new TopicRadius(topic);
            ulong targetRad = (~(ulong)(0)) / 100;


            waitFn x = delegate (Keccak addrHash)
            {
                ulong prefix = BitConverter.ToUInt64(addrHash.Bytes.Take(8).ToArray()) / 100;
                ulong dist = prefix ^ BitConverter.ToUInt64(rad.topicHashPrefix);
                long relDist = (long)(dist) / (long)(targetRad);
                double relTime = (1 - relDist / 2) * 2;
                if (relTime < 0)
                {
                    relDist = 0;
                }
                return new TimeSpan(rad._targetWaitTime.Ticks * (long)relTime);
            };
            int bcnt = 0;
            int cnt = 0;
            double sum = 0;
            while (cnt < 100)
            {
                Keccak addr = rad.nextTarget(false).target;
                TimeSpan wait = x(addr);
                INode n = new FakeStatsModelNode(addr);
                Topic[] topics = new Topic[] { new Topic("foo") };

                Ticket t = new Ticket(
                    0,
                    n,
                    new List<Topic>(topics),
                    new PongMessage(),
                    new List<long>(new long[] { wait.Ticks } )
                );

                rad.adjustWithTicket(now, addr, new TicketRef(t, 0));

                if (rad.radius != rad._maxRadius)
                {
                    cnt++;
                    sum += (double)(rad.radius);
                }
                else
                {
                    bcnt++;
                    if (bcnt > 500)
                    {
                        //_logger.Trace("Radius did not converge in 500 iterations");
                    }
                }
                double avgRel = sum / (double)cnt / (double)targetRad;
                if (avgRel > 1.05 || avgRel < .95)
                {
                    //_logger.Trace($"Average target/ratio is too far from 1 { avgRel }");
                }
            }
        }

            [Test]
            public void TestSimTopicHierarchy()
        {
            //• Make 1024 DiscoveryManagers/Apps
            // • for each node of the 1024, launch into simulation:            
        }
    }
}