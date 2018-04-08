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

using System.Net;
using Nethermind.Network;

namespace Nethermind.Discovery
{
    public class DiscoveryConfigurationProvider : IDiscoveryConfigurationProvider
    {
        public DiscoveryConfigurationProvider(INetworkHelper networkHelper)
        {
            PongTimeout = 15000;
            BucketSize = 16;
            BucketsCount = 256;
            MasterExternalIp = networkHelper.GetExternalIp()?.ToString();
            MasterHost = networkHelper.GetLocalIp()?.ToString() ?? "127.0.0.1";
        }

        public int BucketSize { get; set; }
        public int BucketsCount { get; set; }
        public int Concurrency => 3;
        public int BitsPerHop => 8;
        //public string MasterHost => "127.0.0.1";
        public string MasterHost { get; set; }  //=> "192.168.1.154";
        public string MasterExternalIp { get; set; }
        public int MasterPort => 30304;
        public int MaxDiscoveryRounds => 8;
        public int EvictionCheckInterval => 75;
        public int SendNodeTimeout => 500;
        public int PongTimeout { get; set; }
        public int BootNodePongTimeout => 100000;
        public int PingRetryCount => 3;
        public int DiscoveryInterval => 30000;
        public int DiscoveryNewCycleWaitTime => 50;
        public int RefreshInterval => 7200;

        public (string Id, string Host, int Port)[] BootNodes => new[]
        {
            ("3aaa3978d25dd21ecdf69f53226fa05d68d5ce7340633c8c53bcb52cd4bd5b373f104911a3cc77ac70d4c295774a620eb197c8a45daee2a4ed7bfe9118cc9ee8", MasterHost, 30303)
            //("a1b1771bd94f46c1ad2db852c8d4acd950c8365ca0d6c633ddab0ab51ff87da0170b3f7c9233f1e8a7e3045752fe046b29f3bf84a7f73a8d6294b20662c898a4", "72.88.211.202", 30303)
            //("6ce05930c72abc632c58e2e4324f7c7ea478cec0ed4fa2528982cf34483094e9cbc9216e7aa349691242576d552a2a56aaeae426c5303ded677ce455ba1acd9d", "13.84.180.240", 30303),
            //("20c9ad97c081d63397d7b685a412227a40e23c8bdc6688c6f37e97cfbc22d2b4d1db1510d8f61e6a8866ad7f0e17c02b14182d37ea7c3c8b9c2683aeb6b733a1  ", "52.169.14.227", 30303)
        };

        public string KeyPass => "TestPass";
        public int UdpChannelCloseTimeout => 10000;
        public int PingMessageVersion => 4;
        public int DiscoveryMsgExpiryTime => 60 * 90;
        public int MaxNodeLifecycleManagersCount => 2000;
        public int NodeLifecycleManagersCleaupCount => 200;
    }
}