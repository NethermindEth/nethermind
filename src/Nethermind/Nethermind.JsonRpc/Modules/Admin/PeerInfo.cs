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
        // ignore if empty
        public string? Enr { get; set; }
        public string Enode { get; set; }
        public string Id { get; }
        public string Name { get; set; }
        public string[] Caps { get; set; } // a list of all protocols supported in their canonical names
                                           // e.g snap as snap/1, protocol_name/version_no
        //public Network Network { get; set; } // peerNetworkInfo
        // Network is a {
        //     string LocalAddress
        //     string RemoteAddress
        //     bool Inbound
        //     bool Trusted
        //     bool Static
        // }
        // public Dictionary<string, ProtocolInfo> Protocols { get; set; } // set of protocols supported by the peer
        // it's a map of protocol_name to partial info on protocol some info like version ONLY.

        // ProtocolInfo [or Protocol] with sub-classes Eth, Snap etc...or just


        // delete everthing below!
        public string Host { get; set; }
        public int Port { get; set; }
        public string Address { get; set; }
        public bool IsBootnode { get; set; }
        public bool IsTrusted { get; set; }
        public bool IsStatic { get; set; }

        public string ClientType { get; set; }
        public string EthDetails { get; set; }
        public string LastSignal { get; set; }

        public bool Inbound { get; set; }

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
            Host = IPAddress.Parse(peer.Node.Host!).MapToIPv4().ToString();
            Port = peer.Node.Port;
            Address = peer.Node.Address.ToString();
            IsBootnode = peer.Node.IsBootnode;
            IsStatic = peer.Node.IsStatic;
            Enode = peer.Node.ToString(Node.Format.ENode);
            Inbound = peer.InSession is not null;

            if (includeDetails)
            {
                ClientType = peer.Node.ClientType.ToString();
                EthDetails = peer.Node.EthDetails;
                LastSignal = (peer.InSession ?? peer.OutSession!).LastPingUtc.ToString(CultureInfo.InvariantCulture);

            }
        }
    }
}
