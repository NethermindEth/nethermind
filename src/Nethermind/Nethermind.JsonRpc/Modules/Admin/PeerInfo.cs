// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;
using Nethermind.JsonRpc.Modules.Admin.Utils;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class PeerInfo
    {
        [JsonPropertyName("enode")]
        public string Enode { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("caps")]
        public string[] Caps { get; set; } = Array.Empty<string>();

        [JsonPropertyName("enr")]
        public string? Enr { get; set; }

        [JsonPropertyName("network")]
        public NetworkInfo Network { get; set; } = new();

        [JsonPropertyName("protocols")]
        public Dictionary<string, object> Protocols { get; set; } = new();

        // Optional detailed fields
        [JsonPropertyName("clientType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ClientType { get; set; }

        [JsonPropertyName("ethDetails")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EthDetails { get; set; }

        [JsonPropertyName("lastSignal")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastSignal { get; set; }

        public PeerInfo()
        {
        }

        public PeerInfo(Peer peer, bool includeDetails = false, NodeInfo nodeInfo = null)
        {
            PeerValidator.ValidatePeer(peer);

            SetBasicInfo(peer);
            SetNetworkInfo(peer, nodeInfo);
            SetProtocols(peer);

            if (includeDetails)
            {
                SetDetailedFields(peer);
            }
        }

        private void SetBasicInfo(Peer peer)
        {
            Id = peer.Node.Id.Hash.ToString(false);
            Name = peer.Node.ClientId;
            Enode = peer.Node.ToString(Node.Format.ENode);
            Caps = CapabilityExtractor.ExtractCapabilities(peer);
            Enr = EnrExtractor.GetEnrFromPeer(peer);
        }

        private void SetNetworkInfo(Peer peer, NodeInfo nodeInfo = null)
        {
            bool isInbound = peer.InSession is not null;
            Network = NetworkInfoBuilder.Build(peer, isInbound, nodeInfo);
        }

        private void SetProtocols(Peer peer)
        {
            Protocols = ProtocolInfoBuilder.Build(peer);
        }

        private void SetDetailedFields(Peer peer)
        {
            ClientType = peer.Node.ClientType.ToString();
            EthDetails = peer.Node.EthDetails;
            ISession? session = peer.InSession ?? peer.OutSession;
            LastSignal = session?.LastPingUtc.ToString(CultureInfo.InvariantCulture);
        }
    }
}
