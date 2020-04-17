﻿using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.P2p;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;
using Nethermind.Peering.Mothra;

namespace Nethermind.BeaconNode.Peering
{
    public class MothraNetworkPeering : INetworkPeering
    {
        private readonly ILogger _logger;
        private readonly IMothraLibp2p _mothraLibp2p;
        private readonly PeerManager _peerManager;

        public MothraNetworkPeering(ILogger<MothraNetworkPeering> logger, IMothraLibp2p mothraLibp2p,
            PeerManager peerManager)
        {
            _logger = logger;
            _mothraLibp2p = mothraLibp2p;
            _peerManager = peerManager;
        }

        public Slot HighestPeerSlot => _peerManager.HighestPeerSlot;

        public Slot SyncStartingSlot => _peerManager.SyncStartingSlot;

        public Task DisconnectPeerAsync(string peerId)
        {
            // NOTE: Mothra does not support peer disconnect, so nothing to do.
            _peerManager.DisconnectSession(peerId);
            return Task.CompletedTask;
        }

        public Task PublishBeaconBlockAsync(SignedBeaconBlock signedBlock)
        {
            // TODO: Validate signature before broadcasting (if not already validated)

            Span<byte> encoded = new byte[Ssz.Ssz.SignedBeaconBlockLength(signedBlock)];
            Ssz.Ssz.Encode(encoded, signedBlock);

            LogDebug.GossipSend(_logger, nameof(TopicUtf8.BeaconBlock), encoded.Length, null);
            _mothraLibp2p.SendGossip(TopicUtf8.BeaconBlock, encoded);

            return Task.CompletedTask;
        }

        public Task RequestBlocksAsync(string peerId, Root peerHeadRoot, Slot finalizedSlot, Slot peerHeadSlot)
        {
            // TODO: BeaconBlocksByRange is separate work item... just log that we got here.

            LogDebug.RpcSend(_logger, RpcDirection.Request, nameof(MethodUtf8.BeaconBlocksByRange), peerId, 0, null);
            //throw new NotImplementedException();

            return Task.CompletedTask;
        }

        public Task SendStatusAsync(string peerId, RpcDirection rpcDirection, PeeringStatus peeringStatus)
        {
            byte[] peerUtf8 = Encoding.UTF8.GetBytes(peerId);
            Span<byte> encoded = new byte[Ssz.Ssz.PeeringStatusLength];
            Ssz.Ssz.Encode(encoded, peeringStatus);

            if (_logger.IsDebug())
                LogDebug.RpcSend(_logger, rpcDirection, nameof(MethodUtf8.Status), peerId, encoded.Length, null);
            if (rpcDirection == RpcDirection.Request)
            {
                _mothraLibp2p.SendRpcRequest(MethodUtf8.Status, peerUtf8, encoded);
            }
            else
            {
                _mothraLibp2p.SendRpcResponse(MethodUtf8.Status, peerUtf8, encoded);
            }

            return Task.CompletedTask;
        }
    }
}