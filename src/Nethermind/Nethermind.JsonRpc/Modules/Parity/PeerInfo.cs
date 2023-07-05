// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats.Model;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class PeerInfo
    {
        [JsonProperty("id", Order = 0)]
        public string Id { get; set; }

        [JsonProperty("name", Order = 1)]
        public string Name { get; set; }

        [JsonProperty("caps", Order = 2)]
        public List<string> Caps { get; set; }

        [JsonProperty("network", Order = 3)]
        public PeerNetworkInfo Network { get; set; }

        [JsonProperty("protocols", Order = 4)]
        public Dictionary<string, EthProtocolInfo> Protocols { get; set; }

        public PeerInfo(Peer peer)
        {
            ISession session = peer.InSession ?? peer.OutSession;
            PeerNetworkInfo peerNetworkInfo = new();
            EthProtocolInfo ethProtocolInfo = new();
            Caps = new List<string>();

            if (peer.Node is not null)
            {
                Name = peer.Node.ClientId;
                peerNetworkInfo.LocalAddress = peer.Node.Host;
            }

            if (session is not null)
            {
                Id = session.RemoteNodeId.ToString();
                peerNetworkInfo.RemoteAddress = session.State != SessionState.New ? session.RemoteHost : "Handshake";

                if (session.TryGetProtocolHandler(Protocol.Eth, out var handler))
                {
                    ethProtocolInfo.Version = handler.ProtocolVersion;
                    if (handler is ISyncPeer syncPeer)
                    {
                        ethProtocolInfo.Difficulty = syncPeer.TotalDifficulty;
                        ethProtocolInfo.HeadHash = syncPeer.HeadHash;
                    }
                }

                if (session.TryGetProtocolHandler(Protocol.P2P, out var p2PHandler))
                {
                    if (p2PHandler is IP2PProtocolHandler p2PProtocolHandler)
                    {
                        foreach (Capability capability in p2PProtocolHandler.AgreedCapabilities)
                        {
                            Caps.Add(string.Concat(capability.ProtocolCode, "/", capability.Version));
                        }
                    }
                }
            }

            Network = peerNetworkInfo;

            Protocols = new Dictionary<string, EthProtocolInfo>
            {
                { "eth", ethProtocolInfo }
            };
        }
    }
}
