// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Nethermind.Network.P2P;
using Nethermind.Network;
using Nethermind.Stats.Model;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class NetworkInfoBuilder
    {
        public static NetworkInfo Build(Peer peer, bool isInbound)
        {
            if (peer?.Node == null)
            {
                throw new ArgumentNullException(nameof(peer));
            }

            var session = peer.InSession ?? peer.OutSession;
            var localAddress = GetLocalAddress(peer);
            var localHost = GetLocalHost(localAddress);
            var remoteAddress = GetRemoteAddress(session, peer.Node);

            return new NetworkInfo
            {
                LocalAddress = localAddress,
                LocalHost = localHost,
                RemoteAddress = remoteAddress,
                Inbound = isInbound,
                Trusted = peer.Node.IsTrusted,
                Static = peer.Node.IsStatic
            };
        }

        private static string GetLocalAddress(Peer peer)
        {
            // For backward compatibility with subscriptions, use the peer's address
            // This matches the old peerInfo.Host behavior
            if (peer.Node?.Host != null)
            {
                return $"{IPAddress.Parse(peer.Node.Host).MapToIPv4()}:{peer.Node.Port}";
            }

            return string.Empty;
        }

        private static string GetLocalHost(string localAddress)
        {
            return ExtractHost(localAddress);
        }

        private static string ExtractHost(string address)
        {
            if (string.IsNullOrEmpty(address))
                return string.Empty;

            var colonIndex = address.LastIndexOf(':');
            return colonIndex > 0 ? address.Substring(0, colonIndex) : address;
        }

        private static string GetRemoteAddress(ISession? session, Node node)
        {
            if (session != null)
            {
                return $"{session.RemoteHost}:{session.RemotePort}";
            }

            return node.Address.ToString();
        }
    }
}
