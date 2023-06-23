// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Transport.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
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
        bool IsNetworkIdMatched { get; set; }
        ConnectionDirection Direction { get; }
        Guid SessionId { get; }
        Node Node { get; }
        DateTime LastPingUtc { get; set; }
        DateTime LastPongUtc { get; set; }
        void ReceiveMessage(Packet packet);
        void ReceiveMessage(ZeroPacket zeroPacket);
        int DeliverMessage<T>(T message) where T : P2PMessage;
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
        void InitiateDisconnect(InitiateDisconnectReason disconnectReason, string details);

        /// <summary>
        ///  Drop tcp connection after a delay
        /// </summary>
        void MarkDisconnected(EthDisconnectReason ethDisconnectReason, DisconnectType disconnectType, string details);

        void Handshake(PublicKey handshakeRemoteNodeId);

        void StartTrackingSession();

        event EventHandler<DisconnectEventArgs> Disconnecting;
        event EventHandler<DisconnectEventArgs> Disconnected;
        event EventHandler<EventArgs> Initialized;
        event EventHandler<EventArgs> HandshakeComplete;
    }
}
