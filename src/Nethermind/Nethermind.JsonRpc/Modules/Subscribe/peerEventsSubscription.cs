// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Blockchain;
using Nethermind.Network.P2P;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class PeerEventsSubscription : Subscription
    {
        private readonly IPeerPool _peerPool;
        private readonly ISession _session;


        public PeerEventsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            ILogManager? logManager,
            IPeerPool? peerPool,
            ISession? session
            )
            : base(jsonRpcDuplexClient)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _session = session ?? throw new ArgumentNullException(nameof(session));

            _peerPool.PeerAdded += OnPeerAdded;
            _peerPool.PeerRemoved += OnPeerRemoved;
            _session.MsgReceived += OnMsgReceived;
            _session.MsgDelivered += OnMsgDelivered;
            if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} will track PeerAdded and PeerRemoved.");
        }

        private void OnPeerAdded(object? sender, PeerEventArgs args)
        {
            PeerInfo peerInfo = new(new Peer(args.node), false);
            var response = new PeerEventResponse
            {
                Type = PeerEventType.Add,
                Peer =  peerInfo.Id,
                Local = peerInfo.Host,
                Remote = peerInfo.Address,
            };

            ScheduleAction(async () =>
            {
                using JsonRpcResult result = CreateSubscriptionMessage(
                    response,
                    SubscriptionMethodName.AdminSubscription
                    );
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} printed new peer.");
            });
        }

        private void OnPeerRemoved(object? sender, PeerEventArgs args)
        {
            PeerInfo peerInfo = new(new Peer(args.node), false);
            var response = new PeerEventResponse
            {
                Type = PeerEventType.Drop,
                Peer = peerInfo.Id,
                Local = peerInfo.Host,
                Remote = peerInfo.Address,
            };

            ScheduleAction(async () =>
            {
                using JsonRpcResult result = CreateSubscriptionMessage(
                    response,
                    SubscriptionMethodName.AdminSubscription
                    );
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} printed dropped peer.");
            });
        }

        private void OnMsgReceived(object? sender, PeerEventArgs args)
        {
            // [Complete implementation]
            PeerInfo peerInfo = new(new Peer(args.node), false);
            var response = new PeerEventResponse
            {
                Type = PeerEventType.MsgRecv,
                Peer = peerInfo.Id,
                Local = peerInfo.Host,
                Remote = peerInfo.Address,
                protocal = args.messageInfo.protocol,
                MsgPacketType = args.messageInfo.packetType,
                MsgSize = args.messageInfo.size,
            };
            ScheduleAction(async () =>
            {
                using JsonRpcResult result = CreateSubscriptionMessage(new PeerMsgSendRecvResponse(args, "MsgRecv", null), SubscriptionMethodName.AdminSubscription);
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} printed message received.");
            });
        }

        private void OnMsgDelivered(object? sender, PeerEventArgs args)
        {
            // [Complete implementation]
            PeerInfo peerInfo = new(new Peer(args.node), false);
            var response = new PeerEventResponse
            {
                Type = PeerEventType.MsgRecv,
                Peer = peerInfo.Id,
                Local = peerInfo.Host,
                Remote = peerInfo.Address,
                protocal = args.messageInfo.protocol,
                MsgPacketType = args.messageInfo.packetType,
                MsgSize = args.messageInfo.size,
            };
            ScheduleAction(async () =>
            {
                using JsonRpcResult result = CreateSubscriptionMessage(new PeerMsgSendRecvResponse(args, "MsgSend", null), SubscriptionMethodName.AdminSubscription);
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} printed message sned.");
            });
        }

        public override string Type => SubscriptionType.AdminSubscription.PeerEvents;

        public override void Dispose()
        {
            _peerPool.PeerAdded -= OnPeerAdded;
            _peerPool.PeerRemoved -= OnPeerRemoved;
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"admin_subscription.peerEvent {Id} is no longer subscribed.");
        }

        public static class PeerEventType
        {
            public static string Add = "add";
            public static string Drop = "drop";
            public static string MsgSend = "msgsend";
            public static string MsgRecv = "msgrecv";
        }
    }

}
