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

namespace Nevermind.Discovery
{
    public class DiscoveryConfigurationProvider : IDiscoveryConfigurationProvider
    {
        public int BucketSize => 16;
        public int BucketsCount => 256;
        public int Concurrency => 3;
        public int BitsPerHop => 8;
        public string MasterHost => "localhost";
        public int MasterPort => 10000;
        public int MaxDiscoveryRounds => 8;
        public int EvictionCheckInterval => 75;
        public int SendNodeTimeout => 300;
        public int PongTimeout => 15000;
        public int PingRetryCount => 3;
    }
}