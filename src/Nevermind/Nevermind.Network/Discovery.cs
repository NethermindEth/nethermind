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

namespace Nevermind.Network
{
    public static class Discovery
    {
        public static string[] BootstrapNodes { get; } =
        {
            "a",
            "b"
        };

        /// <summary>
        /// Kademlia's 'k'
        /// </summary>
        public static int BucketSize { get; } = 16;
        
        /// <summary>
        /// Kademlia's 'alpha'
        /// </summary>
        public static int Concurrency { get; } = 3;
        
        /// <summary>
        /// Kademlia's 'b'
        /// </summary>
        public static int BitsPerHop { get; } = 8;
        
        public static TimeSpan EvictionCheckIntervalMiliseconds { get; } = TimeSpan.FromMilliseconds(75);
        
        public static TimeSpan IdleBucketRefreshInterval { get; } = TimeSpan.FromHours(1);
        
        public static TimeSpan PacketValidity { get; } = TimeSpan.FromSeconds(3);
        
        public static int DatagramSizeInBytes { get; } = 1280;
    }
}