// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Net;
using Nethermind.Network;
using Nethermind.Stats.Model;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class PeerInfo
    {
        public string ClientId { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Address { get; set; }
        public bool IsBootnode { get; set; }
        public bool IsTrusted { get; set; }
        public bool IsStatic { get; set; }
        public string Enode { get; set; }

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

            ClientId = peer.Node.ClientId;
            Host = peer.Node.Host is null ? null : IPAddress.Parse(peer.Node.Host).MapToIPv4().ToString();
            Port = peer.Node.Port;
            Address = peer.Node.Address.ToString();
            IsBootnode = peer.Node.IsBootnode;
            IsStatic = peer.Node.IsStatic;
            Enode = peer.Node.ToString(Node.Format.ENode);

            if (includeDetails)
            {
                ClientType = peer.Node.ClientType.ToString();
                EthDetails = peer.Node.EthDetails;
                LastSignal = (peer.InSession ?? peer.OutSession)?.LastPingUtc.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
