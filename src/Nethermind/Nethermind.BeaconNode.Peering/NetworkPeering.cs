using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Peering.Mothra;
using Nethermind.Ssz;

namespace Nethermind.BeaconNode.Peering
{
    public class NetworkPeering : INetworkPeering
    {
        private readonly ILogger _logger;
        private readonly IMothraLibp2p _mothraLibp2p;

        public NetworkPeering(ILogger<PeeringWorker> logger, IMothraLibp2p mothraLibp2p)
        {
            _logger = logger;
            _mothraLibp2p = mothraLibp2p;
        }

        public Task PublishBeaconBlockAsync(BeaconBlock beaconBlock)
        {
            // TODO: Validate signature before broadcasting

            string topic = Topic.BeaconBlock;
            Span<byte> encoded = new byte[Nethermind.Ssz.Ssz.BeaconBlockLength(beaconBlock)];
            Nethermind.Ssz.Ssz.Encode(encoded, beaconBlock);

            LogDebug.GossipSend(_logger, topic, encoded.Length, null);
            _mothraLibp2p.SendGossip(topic, encoded);
            
            return Task.CompletedTask;
        }
    }
}