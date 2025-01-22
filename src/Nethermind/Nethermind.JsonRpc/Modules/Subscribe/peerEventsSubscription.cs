// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Blockchain;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class PeerEventsSubscription : Subscription
    {
        private readonly IPeerPool _peerPool;


        public PeerEventsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            ILogManager? logManager,
            IPeerPool? peerPool)
            : base(jsonRpcDuplexClient)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));

            _peerPool.PeerAdded += OnPeerAdded;
            _peerPool.PeerRemoved += OnPeerRemoved;
            if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} will track PeerAdded and PeerRemoved.");
        }

        private void OnPeerAdded(object? sender, PeerEventArgs peerEventArgs)
        {
            PeerInfo peerInfo = new(peerEventArgs.Peer, false);
            ScheduleAction(async () =>
            {
                using JsonRpcResult result = CreateSubscriptionMessage(new PeerAddDropResponse(peerInfo, "Add", null), SubscriptionMethodName.AdminSubscription);
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} printed new peer.");
            });
        }

        private void OnPeerRemoved(object? sender, PeerEventArgs peerEventArgs)
        {
            PeerInfo peerInfo = new(peerEventArgs.Peer, false); ;

            ScheduleAction(async () =>
            {
                using JsonRpcResult result = CreateSubscriptionMessage(new PeerAddDropResponse(peerInfo, "Drop", null), SubscriptionMethodName.AdminSubscription);
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"admin_subscription {Id} printed dropped peer.");
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
    }
}
