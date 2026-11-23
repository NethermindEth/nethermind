// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Network.P2P.EventArg;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class PeerEventsSubscription : Subscription
    {
        private readonly IPeerPool _peerPool;
        private readonly IRlpxHost _rlpxHost;
        private readonly ISessionMonitor _sessionMonitor;
        private readonly Func<PeerEventResponse, Task> _sendPeerEvent;

        public PeerEventsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            ILogManager? logManager,
            IPeerPool? peerPool,
            IRlpxHost? rlpxHost
            )
            : base(jsonRpcDuplexClient, MaxQueuedEvents)
        {
            _logger = logManager?.GetClassLogger<PeerEventsSubscription>() ?? throw new ArgumentNullException(nameof(logManager));
            _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _rlpxHost = rlpxHost ?? throw new ArgumentNullException(nameof(rlpxHost));
            _sendPeerEvent = SendPeerEvent;

            _peerPool.PeerAdded += OnPeerAdded;
            _peerPool.PeerRemoved += OnPeerRemoved;

            _sessionMonitor = _rlpxHost.SessionMonitor;
            foreach (ISession session in _sessionMonitor.Sessions)
            {
                session.MsgDelivered += OnMsgDelivered;
                session.MsgReceived += OnMsgReceived;
            }
            _rlpxHost.SessionCreated += OnSessionCreated;
            _rlpxHost.SessionDisconnected += OnSessionDisconnected;
            if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} will track PeerAdded, PeerRemoved, MsgDelivered and MsgReceived.");
        }

        // The handlers run on the network's threads, a message's on its p2p path, so they must not throw. An inbound session
        // raises its message events with its node only once something has read it; without one there is nothing to report.
        private void OnPeerAdded(object? sender, PeerEventArgs args)
        {
            if (args.Node is not { } node) return;
            PeerEventResponse response = new()
            {
                Type = PeerEventType.Add,
                Peer = node.Id,
                Local = node.Host,
                Remote = node.Address.ToString(),
            };

            ScheduleAction(_sendPeerEvent, response);
        }

        private void OnPeerRemoved(object? sender, PeerEventArgs args)
        {
            if (args.Node is not { } node) return;
            PeerEventResponse response = new()
            {
                Type = PeerEventType.Drop,
                Peer = node.Id,
                Local = node.Host,
                Remote = node.Address.ToString(),
            };

            ScheduleAction(_sendPeerEvent, response);
        }

        private void OnMsgReceived(object? sender, PeerEventArgs args)
        {
            if (args.Node is not { } node) return;
            PeerEventResponse response = new()
            {
                Type = PeerEventType.MsgRecv,
                Peer = node.Id,
                Local = node.Host,
                Remote = node.Address.ToString(),
                Protocol = args.MessageInfo.Protocol,
                MsgPacketType = args.MessageInfo.PacketType,
                MsgSize = args.MessageInfo.Size,
            };

            ScheduleAction(_sendPeerEvent, response);
        }

        private void OnMsgDelivered(object? sender, PeerEventArgs args)
        {
            if (args.Node is not { } node) return;
            PeerEventResponse response = new()
            {
                Type = PeerEventType.MsgSend,
                Peer = node.Id,
                Local = node.Host,
                Remote = node.Address.ToString(),
                Protocol = args.MessageInfo.Protocol,
                MsgPacketType = args.MessageInfo.PacketType,
                MsgSize = args.MessageInfo.Size,
            };

            ScheduleAction(_sendPeerEvent, response);
        }

        private async Task SendPeerEvent(PeerEventResponse response)
        {
            using JsonRpcResult result = CreateSubscriptionMessage(response, SubscriptionMethodName.AdminSubscription);
            await JsonRpcDuplexClient.SendJsonRpcResult(result);
            if (_logger.IsTrace)
            {
                string? printed = response.Type switch
                {
                    PeerEventType.Add => "new peer",
                    PeerEventType.Drop => "dropped peer",
                    PeerEventType.MsgRecv => "message received",
                    PeerEventType.MsgSend => "message sent",
                    _ => response.Type
                };
                _logger.Trace($"admin_subscription {Id} printed {printed}.");
            }
        }

        private void OnSessionCreated(object? sender, SessionEventArgs args)
        {
            args.Session.MsgDelivered += OnMsgDelivered;
            args.Session.MsgReceived += OnMsgReceived;
        }

        private void OnSessionDisconnected(object sender, ISession session, DisconnectEventArgs e)
        {
            session.MsgDelivered -= OnMsgDelivered;
            session.MsgReceived -= OnMsgReceived;
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
            }
            _rlpxHost.SessionCreated -= OnSessionCreated;
            _rlpxHost.SessionDisconnected -= OnSessionDisconnected;
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
