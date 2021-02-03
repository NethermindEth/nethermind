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
using DotNetty.Transport.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public interface ISession : IDisposable
    {
        byte P2PVersion { get; }
        SessionState State { get; }
        SessionState BestStateReached { get; }
        bool IsClosing { get; }
        PublicKey RemoteNodeId { get; }
        PublicKey ObsoleteRemoteNodeId { get; }
        string RemoteHost { get; set; }
        int RemotePort { get; set; }
        int LocalPort { get; }
        ConnectionDirection Direction { get; }
        Guid SessionId { get; }
        Node Node { get; }
        DateTime LastPingUtc { get; set; }
        DateTime LastPongUtc { get; set; }
        void ReceiveMessage(Packet packet);
        void ReceiveMessage(ZeroPacket zeroPacket);
        void DeliverMessage<T>(T message) where T : P2PMessage;
        void EnableSnappy();
        void AddSupportedCapability(Capability capability);
        bool HasAvailableCapability(Capability capability);
        bool HasAgreedCapability(Capability capability);
      
        IPingSender PingSender { get; set; }
        
        void AddProtocolHandler(IProtocolHandler handler);

        bool TryGetProtocolHandler(string protocolCode, out IProtocolHandler handler);

        void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender);

        /// <summary>
        /// Starts local disconnect (triggers disconnect on each protocolHandler, down to tcp disconnect)
        /// </summary>
        void InitiateDisconnect(DisconnectReason disconnectReason, string details);

        /// <summary>
        ///  Drop tcp connection after a delay
        /// </summary>     
        void MarkDisconnected(DisconnectReason disconnectReason, DisconnectType disconnectType, string details);

        void Handshake(PublicKey handshakeRemoteNodeId);

        event EventHandler<DisconnectEventArgs> Disconnecting;
        event EventHandler<DisconnectEventArgs> Disconnected;
        event EventHandler<EventArgs> Initialized;
        event EventHandler<EventArgs> HandshakeComplete;
    }
}
