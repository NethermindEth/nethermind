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
    public class PeerInfoResult
    {
        // ignore if empty
        public string? Enr { get; set; }
        public string Enode { get; set; }
        public string Id { get; }
        public string Name { get; set; }

        // a list of all protocols supported in their canonical names
        // // e.g snap as snap/1, protocol_name/version_no
        public string[] Caps { get; set; }

        public NetworkInfo Network { get; set; } = new();
        public Dictionary<string, string[]> Protocols { get; set; } = new();


        // keep extra info not availibale in get?
        public bool IsBootnode { get; set; }
        public string ClientType { get; set; }
        public string EthDetails { get; set; }
        public string LastSignal { get; set; }

        // MOVE these to module description.
        // public Network Network { get; set; } // peerNetworkInfo
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


        // // delete everthing below!
        // public string Host { get; set; }
        // public int Port { get; set; }
        // public string Address { get; set; }

        // public bool IsTrusted { get; set; }
        // public bool IsStatic { get; set; }
        //

        //
        // public bool Inbound { get; set; }

        public PeerInfoResult()
        {
        }

        public PeerInfoResult(Peer peer, bool includeDetails)
        {
            if (peer.Node is null)
            {
                throw new ArgumentException(
                    $"{nameof(PeerInfoResult)} cannot be created for a {nameof(Peer)} with an unknown {peer.Node}");
            }

            Name = peer.Node.ClientId;
            Id = peer.Node.Id.Hash.ToString(false);
            // Caps = peer.Protocols.Select(p => p.name).ToArray() // how it should be


            Enode = peer.Node.ToString(Node.Format.ENode);
            Network.Inbound = peer.InSession is not null;
            Network.RemoteAddress = peer.Node.Address.ToString(); // verify this is the peers remote address
            Network.Static = peer.Node.IsStatic;
            // Network.LocalAddress = // whats the diff between remote and local address since peers usually aren't on the same network?.

            IsBootnode = peer.Node.IsBootnode;
            // Host = IPAddress.Parse(peer.Node.Host!).MapToIPv4().ToString();
            // Port = peer.Node.Port;
            // Address = peer.Node.Address.ToString();
            // IsStatic = peer.Node.IsStatic;

            if (includeDetails)
            {
                // for each protocol in the peers protocols add key - value to the dictionary where
                // value is a map of info: value}


                ClientType = peer.Node.ClientType.ToString();
                EthDetails = peer.Node.EthDetails;
                LastSignal = (peer.InSession ?? peer.OutSession!).LastPingUtc.ToString(CultureInfo.InvariantCulture);

            }
        }
    }

    public class NetworkInfo // maybe change to struct
    {
        public string LocalAddress { get; set; }
        public string RemoteAddress { get; set; }
        public bool Inbound { get; set; }
        public bool Trusted { get; set; } // how to get this? changed from IsTrusted to Trusted (what geth uses)
        public bool Static { get; set; } // changed from IsStatic to Static (what geth uses)
    }
}
