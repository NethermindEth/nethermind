﻿/*
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

using System.IO;

namespace Nethermind.Network.Config
{
    public class NetworkConfig : INetworkConfig
    {
        public int BucketSize { get; set; } = 16;
        public int BucketsCount { get; set; } = 256;
        public int Concurrency { get; set; } = 3;

        public int BitsPerHop { get; set; } = 8;

        //public string MasterHost => "127.0.0.1";
        public string MasterHost { get; set; } = "127.0.0.1"; //=> "192.168.1.154";
        public string MasterExternalIp { get; set; } = "127.0.0.1";
        public int MasterPort { get; set; } = 30304;
        public int MaxDiscoveryRounds { get; set; } = 8;
        public int EvictionCheckInterval { get; set; } = 75;
        public int SendNodeTimeout { get; set; } = 500;
        public int PongTimeout { get; set; } = 1000 * 15;
        public int BootnodePongTimeout { get; set; } = 1000 * 100;
        public int PingRetryCount { get; set; } = 3;
        public int DiscoveryInterval { get; set; } = 1000 * 30;
        public int DiscoveryPersistenceInterval { get; set; } = 1000 * 5;
        public int DiscoveryNewCycleWaitTime { get; set; } = 50;
        //public int RefreshInterval { get; set; } = 33000;

        public string Bootnodes { get; set; } = string.Empty;

        public string KeyPass { get; set; } = "TestPass";
        public int UdpChannelCloseTimeout { get; set; } = 1000 * 5;
        public string PingMessageVersion { get; set; } = "temporary discovery v5";
        public int DiscoveryMsgExpiryTime { get; set; } = 60 * 90;
        public int MaxNodeLifecycleManagersCount { get; set; } = 2000;
        public int NodeLifecycleManagersCleanupCount { get; set; } = 200;
        
        public string TrustedPeers { get; set; } = string.Empty;
        public string DbBasePath { get; set; } = Path.GetTempPath();
        public bool IsDiscoveryNodesPersistenceOn { get; set; } = true;
        public bool IsPeersPersistenceOn { get; set; } = true;
        public int ActivePeerUpdateInterval { get; set; } = 1000 * 3;
        public bool IsActivePeerTimerEnabled { get; set; } = true;
        public int ActivePeersMaxCount { get; set; } = 25;
        public int DisconnectDelay { get; set; } = 1000 * 60 * 5;
        public int FailedConnectionDelay { get; set; } = 1000 * 60 * 10;
        public int PeersPersistenceInterval { get; set; } = 1000 * 5;
        public int P2PPingInterval { get; set; } = 1000 * 10;
        public int P2PPingRetryCount { get; set; } = 3;
        public string DetailedTimeDateFormat { get; } = "yyyy-MM-dd HH:mm:ss.fff";
        public int MaxPersistedPeerCount { get; set; } = 2000;
        public int PersistedPeerCountCleanupThreshold { get; set; } = 2200;
        public int MaxCandidatePeerCount { get; set; } = 10000;
        public int CandidatePeerCountCleanupThreshold { get; set; } = 11000;

        // v5

        public int MaxNoAdjust { get; set; } = 20;
        public int MinPeakSize { get; set; } = 40;
        public int MinRightSum { get; set; } = 20;
        public TimeSpan TargetWaitTime { get; set; } = new TimeSpan(0, 10, 0);
        public int RadiusBucketsPerBit { get; set; } = 8;

        public int MaxEntries { get; set; } = 10000;
        public int MaxEntriesPerTopic { get; set; } = 50;
        public TimeSpan FallbackRegistrationExpiry = new TimeSpan(1, 0, 0);
        public TimeSpan RegTimeWindow = new TimeSpan(0, 0, 10);
    }
}