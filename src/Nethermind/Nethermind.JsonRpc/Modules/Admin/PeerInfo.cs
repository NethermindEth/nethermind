// SPDX-License-Identifier: LGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.JsonRpc.Modules.Admin.Utils;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class PeerInfo
    {
        private static readonly IReadOnlyList<Capability> EmptyCapabilities = [];

        public string Enode { get; set; } = string.Empty;

        public PublicKey Id { get; set; } = null!;

        public string? Name { get; set; }

        public IReadOnlyList<Capability> Caps { get; set; } = EmptyCapabilities;

        public string? Enr { get; set; }

        public NetworkInfo Network { get; set; } = new();

        public Dictionary<string, object> Protocols { get; set; } = [];

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
            ValidatePeer(peer);

            IReadOnlyList<Capability> capabilities = ExtractCapabilities(peer);

            SetBasicInfo(peer, capabilities);
            SetNetworkInfo(peer);
            SetProtocols(peer, capabilities);

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
            Enr = peer.Node.Enr?.ToString();
        }

        private void SetNetworkInfo(Peer peer)
        {
            bool isInbound = peer.InSession is not null;
            Network = NetworkInfoBuilder.Build(peer, isInbound);
        }

        private void SetProtocols(Peer peer, IReadOnlyList<Capability> capabilities)
        {
            Dictionary<string, object> protocols = [];

            ISession? session = peer.InSession ?? peer.OutSession;
            bool hasEthHandler = TryGetHandlerVersion(session, Protocol.Eth, out int ethVersion);
            bool hasSnapHandler = TryGetHandlerVersion(session, Protocol.Snap, out int snapVersion);

            foreach (Capability capability in capabilities)
            {
                if (capability.ProtocolCode == Protocol.Eth && !hasEthHandler)
                {
                    ethVersion = Math.Max(ethVersion, capability.Version);
                }
                else if (capability.ProtocolCode == Protocol.Snap && !hasSnapHandler)
                {
                    snapVersion = Math.Max(snapVersion, capability.Version);
                }
            }

            // ETH protocol (always present)
            protocols[Protocol.Eth] = new { Version = ethVersion };

            // SNAP protocol (if supported)
            if (snapVersion > 0)
            {
                protocols[Protocol.Snap] = new { Version = snapVersion };
            }

            Protocols = protocols;
        }

        private static bool TryGetHandlerVersion(ISession? session, string protocol, out int version)
        {
            if (session?.TryGetProtocolHandler(protocol, out IProtocolHandler? handler) == true)
            {
                version = handler.ProtocolVersion;
                return true;
            }

            version = 0;
            return false;
        }

        private void SetDetailedFields(Peer peer)
        {
            ClientType = peer.Node.ClientType;
            EthDetails = peer.Node.EthDetails;
            ISession? session = peer.InSession ?? peer.OutSession;
            LastSignal = session?.LastPingUtc;
        }

        private void ValidatePeer(Peer peer)
        {
            ArgumentNullException.ThrowIfNull(peer);

            if (peer.Node is null)
            {
                throw new ArgumentException("Peer must have a valid node", nameof(peer));
            }
        }

        private static IReadOnlyList<Capability> ExtractCapabilities(Peer peer) =>
            (peer.InSession ?? peer.OutSession)?.TryGetProtocolHandler(Protocol.P2P, out IProtocolHandler? handler) == true && handler is IP2PProtocolHandler p2pHandler
                ? p2pHandler.GetCapabilities()
                : EmptyCapabilities;
    }
}
