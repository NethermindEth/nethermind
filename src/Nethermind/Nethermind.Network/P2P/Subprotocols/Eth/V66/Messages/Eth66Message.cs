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

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public abstract class Eth66Message<T> : P2PMessage where T : P2PMessage
    {
        public override int PacketType => EthMessage.PacketType;
        public override string Protocol => EthMessage.Protocol;
        public long RequestId { get; set; } = MessageConstants.Random.NextLong();
        public T EthMessage { get; set; }

        protected Eth66Message() 
        {
        }

        protected Eth66Message(long requestId, T ethMessage)
        {
            RequestId = requestId;
            EthMessage = ethMessage;
        }
        
        public override string ToString()
            => $"{GetType().Name}Eth66({RequestId},{EthMessage})";
    }
}
