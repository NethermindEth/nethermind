// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Stats.Model;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class PeerEventsSubscription : Subscription
    {
        private readonly IPeerPool _peerPool;
        private readonly IRlpxHost _rlpxHost;
        private readonly ISessionMonitor _sessionMonitor;

        public PeerEventsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            ILogManager? logManager,
            IPeerPool? peerPool,
            IRlpxHost? rlpxHost
            )
            : base(jsonRpcDuplexClient)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _rlpxHost = rlpxHost ?? throw new ArgumentNullException(nameof(rlpxHost));

            _peerPool.PeerAdded += OnPeerAdded;
            _peerPool.PeerRemoved += OnPeerRemoved;

            _sessionMonitor = _rlpxHost.SessionMonitor;
            foreach (ISession session in _sessionMonitor.Sessions)
            {
                session.MsgDelivered += OnMsgDelivered;
                session.MsgReceived += OnMsgReceived;
                session.Disconnected += OnSessionDisconnected;
            }
            _rlpxHost.SessionCreated += OnSessionCreated;
            if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} will track PeerAdded, PeerRemoved, MsgDelivered and MsgReceived.");
        }

        private void OnPeerAdded(object? sender, PeerEventArgs args)
        {
            (string localHost, string remoteAddress, PublicKey? peerId) = GetPeerEventInfo(args.Node);
            var response = new PeerEventResponse
            {
                Type = PeerEventType.Add,
                Peer = peerId,
                Local = localHost,
                Remote = remoteAddress,
            };

            ScheduleAction(async () =>
            {
                using JsonRpcResult result = CreateSubscriptionMessage(response, SubscriptionMethodName.AdminSubscription);
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} printed new peer.");
            });
        }

        private void OnPeerRemoved(object? sender, PeerEventArgs args)
        {
            (string localHost, string remoteAddress, PublicKey peerId) = GetPeerEventInfo(args.Node);
            var response = new PeerEventResponse
            {
                Type = PeerEventType.Drop,
                Peer = peerId,
                Local = localHost,
                Remote = remoteAddress,
            };

            ScheduleAction(async () =>
            {
                using JsonRpcResult result = CreateSubscriptionMessage(response, SubscriptionMethodName.AdminSubscription);
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} printed dropped peer.");
            });
        }

        private void OnMsgReceived(object? sender, PeerEventArgs args)
        {
            (string localHost, string remoteAddress, PublicKey peerId) = GetPeerEventInfo(args.Node);
            var response = new PeerEventResponse
            {
                Type = PeerEventType.MsgRecv,
                Peer = peerId,
                Local = localHost,
                Remote = remoteAddress,
                Protocol = args.MessageInfo.Protocol,
                MsgPacketType = args.MessageInfo.PacketType,
                MsgSize = args.MessageInfo.Size,
            };

            ScheduleAction(async () =>
            {
                using JsonRpcResult result = CreateSubscriptionMessage(response, SubscriptionMethodName.AdminSubscription);
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} printed message received.");
            });
        }

        private void OnMsgDelivered(object? sender, PeerEventArgs args)
        {
            (string localHost, string remoteAddress, PublicKey peerId) = GetPeerEventInfo(args.Node);
            var response = new PeerEventResponse
            {
                Type = PeerEventType.MsgSend,
                Peer = peerId,
                Local = localHost,
                Remote = remoteAddress,
                Protocol = args.MessageInfo.Protocol,
                MsgPacketType = args.MessageInfo.PacketType,
                MsgSize = args.MessageInfo.Size,
            };

            ScheduleAction(async () =>
            {
                using JsonRpcResult result = CreateSubscriptionMessage(response, SubscriptionMethodName.AdminSubscription);
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} printed message sent.");
            });
        }

        private (string localHost, string remoteAddress, PublicKey peerId) GetPeerEventInfo(Node node)
        {
            PeerInfo peerInfo = new(new Peer(node));

            return (
                peerInfo.Network.LocalHost,
                peerInfo.Network.RemoteAddress,
                peerInfo.Id
            );
        }

        private void OnSessionCreated(object? sender, SessionEventArgs args)
        {
            args.Session.MsgDelivered += OnMsgDelivered;
            args.Session.MsgReceived += OnMsgReceived;
            args.Session.Disconnected += OnSessionDisconnected;
        }

        private void OnSessionDisconnected(object sender, DisconnectEventArgs e)
        {
            ISession session = (ISession)sender;
            session.MsgDelivered -= OnMsgDelivered;
            session.MsgReceived -= OnMsgReceived;
            session.Disconnected -= OnSessionDisconnected;
        }

        public override string Type => SubscriptionType.AdminSubscription.PeerEvents;

        public override void Dispose()
        {
            _peerPool.PeerAdded -= OnPeerAdded;
            _peerPool.PeerRemoved -= OnPeerRemoved;
            foreach (ISession session in _sessionMonitor.Sessions)
            {
                session.MsgDelivered -= OnMsgDelivered;
                session.MsgReceived -= OnMsgReceived;
                session.Disconnected -= OnSessionDisconnected;
            }
            _rlpxHost.SessionCreated -= OnSessionCreated;
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"admin_subscription.peerEvent {Id} is no longer subscribed.");
        }

        private static class PeerEventType
        {
            public const string Add = "add";
            public const string Drop = "drop";
            public const string MsgSend = "msgsend";
            public const string MsgRecv = "msgrecv";
        }
    }
}
