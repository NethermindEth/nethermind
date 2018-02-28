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

using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class P2PSessionFactory : ISessionFactory
    {
        private readonly int _listenPort;
        private readonly PublicKey _localNodeId;

        public P2PSessionFactory(PublicKey localNodeId, int listenPort)
        {
            _localNodeId = localNodeId;
            _listenPort = listenPort;
        }

        public ISession Create(IMessageSender messageSender)
        {
            P2PSession p2PSession = new P2PSession(messageSender, _localNodeId, _listenPort);
            return p2PSession;
        }
    }
}