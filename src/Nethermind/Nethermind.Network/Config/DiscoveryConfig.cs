//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Config;

namespace Nethermind.Network.Config
{
    public class DiscoveryConfig : IDiscoveryConfig
    {
        [ConfigItem(HiddenFromDocs = true)]
        public int BucketSize { get; set; } = 16;
        [ConfigItem(HiddenFromDocs = true)]
        public int BucketsCount { get; set; } = 256;
        [ConfigItem(HiddenFromDocs = true)]
        public int Concurrency { get; set; } = 3;
        [ConfigItem(HiddenFromDocs = true)]
        public int BitsPerHop { get; set; } = 8;
        [ConfigItem(HiddenFromDocs = true)]
        public int MaxDiscoveryRounds { get; set; } = 8;
        [ConfigItem(HiddenFromDocs = true)]
        public int EvictionCheckInterval { get; set; } = 75;
        [ConfigItem(HiddenFromDocs = true)]
        public int SendNodeTimeout { get; set; } = 500;
        [ConfigItem(HiddenFromDocs = true)]
        public int PongTimeout { get; set; } = 1000 * 15;
        [ConfigItem(HiddenFromDocs = true)]
        public int BootnodePongTimeout { get; set; } = 1000 * 100;
        [ConfigItem(HiddenFromDocs = true)]
        public int PingRetryCount { get; set; } = 3;
        [ConfigItem(HiddenFromDocs = true)]
        public int DiscoveryInterval { get; set; } = 1000 * 30;
        [ConfigItem(HiddenFromDocs = true)]
        public int DiscoveryPersistenceInterval { get; set; } = 1000 * 180;
        [ConfigItem(HiddenFromDocs = true)]
        public int DiscoveryNewCycleWaitTime { get; set; } = 50;
        [ConfigItem(HiddenFromDocs = true)]
        public int UdpChannelCloseTimeout { get; set; } = 1000 * 5;
        [ConfigItem(HiddenFromDocs = true)]
        public int MaxNodeLifecycleManagersCount { get; set; } = 8000;
        [ConfigItem(HiddenFromDocs = true)]
        public int NodeLifecycleManagersCleanupCount { get; set; } = 4000;
        [ConfigItem(HiddenFromDocs = true)]
        public string Bootnodes { get; set; } = string.Empty;
    }
}
