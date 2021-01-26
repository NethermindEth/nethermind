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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Messages
{
    public class DiscoveryMessageFactory : IDiscoveryMessageFactory
    {
        /// <summary>
        /// This is the value set by other clients based on real network tests.
        /// </summary>
        private const int ExpirationTimeInSeconds = 20;
        
        private readonly ITimestamper _timestamper;

        public DiscoveryMessageFactory(ITimestamper timestamper)
        {
            _timestamper = timestamper;
        }

        public T CreateOutgoingMessage<T>(Node destination) where T : DiscoveryMessage
        {
            T message = Activator.CreateInstance<T>();
            message.FarAddress = destination.Address;
            message.ExpirationTime = ExpirationTimeInSeconds + _timestamper.UnixTime.SecondsLong;
            return message;
        }

        public T CreateIncomingMessage<T>(PublicKey farPublicKey) where T : DiscoveryMessage
        {
            T message = Activator.CreateInstance<T>();
            message.FarPublicKey = farPublicKey;
            return message;
        }
    }
}
