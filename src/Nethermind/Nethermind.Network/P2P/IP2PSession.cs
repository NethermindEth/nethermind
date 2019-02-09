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
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Core.Model;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public interface IP2PSession : IDisposable
    {
        byte P2PVersion { get; }
        SessionState SessionState { get; }
        bool IsClosing { get; }
        PublicKey RemoteNodeId { get; set; }
        PublicKey ObsoleteRemoteNodeId { get; set; }
        string RemoteHost { get; set; }
        int? RemotePort { get; set; }
        ConnectionDirection ConnectionDirection { get; }
        Guid SessionId { get; }
        Node Node { get; }
        void ReceiveMessage(Packet packet);
        void DeliverMessage(Packet packet);
        void EnableSnappy();
      
        IP2PMessageSender P2PMessageSender { get; set; }
        
        void AddProtocolHandler(IProtocolHandler handler);
        
        void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender);

        /// <summary>
        /// Starts local disconnect (triggers disconnect on each protocolHandler, down to tcp disconnect)
        /// </summary>
        Task InitiateDisconnectAsync(DisconnectReason disconnectReason);

        /// <summary>
        ///  Drop tcp connection after a delay
        /// </summary>     
        Task DisconnectAsync(DisconnectReason disconnectReason, DisconnectType disconnectType);

        void Handshake(PublicKey handshakeRemoteNodeId);

        event EventHandler<DisconnectEventArgs> Disconnecting;
        event EventHandler<DisconnectEventArgs> Disconnected;
        event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        event EventHandler<EventArgs> Initialized;
        event EventHandler<EventArgs> HandshakeComplete;
    }
}