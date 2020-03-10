using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Peering.Mothra;

namespace Nethermind.BeaconNode.Peering
{
    public class MothraNetworkPeering : INetworkPeering
    {
        private readonly ILogger _logger;
        private readonly IMothraLibp2p _mothraLibp2p;
        private readonly PeerSyncStatus _peerSyncStatus;

        public MothraNetworkPeering(ILogger<MothraNetworkPeering> logger, IMothraLibp2p mothraLibp2p, PeerSyncStatus peerSyncStatus)
        {
            _logger = logger;
            _mothraLibp2p = mothraLibp2p;
            _peerSyncStatus = peerSyncStatus;
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

        public Slot HighestPeerSlot => _peerSyncStatus.HighestPeerSlot;
        
        public Slot SyncStartingSlot => _peerSyncStatus.SyncStartingSlot;
    }
}