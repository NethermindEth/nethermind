// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using Nethermind.Network;
using Nethermind.Stats.Model;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class PeerInfo
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Enr { get; set; }
        public string Enode { get; set; }
        public string Id { get; }
        public string Name { get; set; }

        // a list of all protocols supported in their canonical names
        // // e.g snap as snap/1, protocol_name/version_no
        public string[] Caps { get; set; }

        public NetworkInfo Network { get; set; }
        public Dictionary<string, string[]> Protocols { get; set; } = new();// set of protocols supported by the peer
        // it's a map of protocol_name to partial info on protocol some info like version ONLY.
        // ProtocolInfo [or Protocol] with sub-classes Eth, Snap etc...or just


        // keep extra info not availibale in get?
        public bool IsBootnode { get; set; }
        public string ClientType { get; set; }
        public string EthDetails { get; set; }
        public string LastSignal { get; set; }

        public PeerInfo()
        {
        }

        public PeerInfo(Peer peer, bool includeDetails)
        {
            if (peer.Node is null)
            {
                throw new ArgumentException(
                    $"{nameof(PeerInfo)} cannot be created for a {nameof(Peer)} with an unknown {peer.Node}");
            }

            Name = peer.Node.ClientId;
            Id = peer.Node.Id.Hash.ToString(false);
            // Caps = peer.Protocols.Select(p => p.name).ToArray() // how it should be


            Enode = peer.Node.ToString(Node.Format.ENode);
            Network = new()
            {
                Inbound = peer.InSession is not null,
                RemoteAddress = peer.Node.Address.ToString(),
                Static = peer.Node.IsStatic

            };
            IsBootnode = peer.Node.IsBootnode;

            if (includeDetails)
            {
                ClientType = peer.Node.ClientType.ToString();
                EthDetails = peer.Node.EthDetails;
                LastSignal = (peer.InSession ?? peer.OutSession!).LastPingUtc.ToString(CultureInfo.InvariantCulture);

            }
        }
    }

    public class NetworkInfo
    {
        public string LocalAddress { get; set; }
        public string RemoteAddress { get; set; }
        public bool Inbound { get; set; }
        public bool Trusted { get; set; }
        public bool Static { get; set; }
    }
}
