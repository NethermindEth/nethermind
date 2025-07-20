// SPDX-License-Identifier: LGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;
using Nethermind.Network.Enr;
using Nethermind.JsonRpc.Modules.Admin.Converters;
using Nethermind.Serialization.Json;
using Nethermind.Network.Contract;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.JsonRpc.Modules.Admin.Models;
using Nethermind.JsonRpc.Modules.Admin.Utils;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class PeerInfo
    {
        private static readonly IReadOnlyList<Capability> EmptyCapabilities = Array.Empty<Capability>();

        public string Enode { get; set; } = string.Empty;

        [JsonConverter(typeof(PublicKeyHashedConverter))]
        public PublicKey Id { get; set; } = null!;

        public string? Name { get; set; }

        [JsonConverter(typeof(CapabilityConverter))]
        public IReadOnlyList<Capability> Caps { get; set; } = EmptyCapabilities;

        public string? Enr { get; set; }

        public NetworkInfo Network { get; set; } = new();

        public Dictionary<string, object> Protocols { get; set; } = new();

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public NodeClientType? ClientType { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EthDetails { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? LastSignal { get; set; }

        public PeerInfo()
        {
        }

        public PeerInfo(Peer peer, bool includeDetails = false)
        {
            PeerValidator.ValidatePeer(peer);

            var capabilities = ExtractCapabilities(peer);

            SetBasicInfo(peer, capabilities);
            SetNetworkInfo(peer);
            SetProtocols(capabilities);

            if (includeDetails)
            {
                SetDetailedFields(peer);
            }
        }

        private void SetBasicInfo(Peer peer, IReadOnlyList<Capability> capabilities)
        {
            Id = peer.Node.Id;
            Name = peer.Node.ClientId;
            Enode = peer.Node.ToString(Node.Format.ENode);
            Caps = capabilities;
            Enr = EnrExtractor.GetEnrFromPeer(peer);
        }

        private void SetNetworkInfo(Peer peer)
        {
            bool isInbound = peer.InSession is not null;
            Network = NetworkInfoBuilder.Build(peer, isInbound);
        }

        private void SetProtocols(IReadOnlyList<Capability> capabilities)
        {
            var protocols = new Dictionary<string, object>();

            int ethVersion = 0;
            int snapVersion = 0;

            for (int i = 0; i < capabilities.Count; i++)
            {
                var capability = capabilities[i];

                if (capability.ProtocolCode == Protocol.Eth && ethVersion == 0)
                {
                    ethVersion = capability.Version;
                }
                else if (capability.ProtocolCode == Protocol.Snap && snapVersion == 0)
                {
                    snapVersion = capability.Version;
                }

                if (ethVersion > 0 && snapVersion > 0) break;
            }

            // ETH protocol (always present)
            protocols[Protocol.Eth] = new EthProtocolDetails { Version = ethVersion };

            // SNAP protocol (if supported)
            if (snapVersion > 0)
            {
                protocols[Protocol.Snap] = new { version = snapVersion };
            }

            Protocols = protocols;
        }

        private void SetDetailedFields(Peer peer)
        {
            ClientType = peer.Node.ClientType;
            EthDetails = peer.Node.EthDetails;
            ISession? session = peer.InSession ?? peer.OutSession;
            LastSignal = session?.LastPingUtc;
        }

        private static IReadOnlyList<Capability> ExtractCapabilities(Peer peer)
        {
            var session = peer.InSession ?? peer.OutSession;
            if (session?.TryGetProtocolHandler(Protocol.P2P, out IProtocolHandler? handler) == true &&
                handler is IP2PProtocolHandler p2pHandler)
            {
                var capabilities = p2pHandler.GetCapabilitiesForAdmin();
                return capabilities is IReadOnlyList<Capability> readOnlyList ? readOnlyList : capabilities.ToArray();
            }

            return EmptyCapabilities;
        }

        public int GetEthVersion()
        {
            for (int i = 0; i < Caps.Count; i++)
            {
                if (Caps[i].ProtocolCode == Protocol.Eth)
                {
                    return Caps[i].Version;
                }
            }
            return 0;
        }
    }
}
