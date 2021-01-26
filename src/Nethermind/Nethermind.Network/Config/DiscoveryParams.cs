//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

namespace Nethermind.Network.Config
{
    public static class DiscoveryParams
    {
        public const int BucketSize = 16;
        public const int BucketsCount = 256;
        public const int Concurrency = 3;
        public const int BitsPerHop = 8;
        public const int MaxDiscoveryRounds = 8;
        public const int EvictionCheckInterval = 75;
        public const int SendNodeTimeout = 500;
        public const int PongTimeout = 1000 * 15;
        public const int BootnodePongTimeout = 1000 * 100;
        public const int PingRetryCount = 3;
        public const int DiscoveryInterval = 1000 * 30;
        public const int DiscoveryPersistenceInterval = 1000 * 180;
        public const int DiscoveryNewCycleWaitTime = 50;
        public const int UdpChannelCloseTimeout = 1000 * 5;
        public const int MaxNodeLifecycleManagersCount = 8000;
        public const int NodeLifecycleManagersCleanupCount = 4000;
        public const bool IsDiscoveryNodesPersistenceOn = true;
    }
}
