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
            var (localAddress, localHost) = GetLocalInfo(peer.Node);
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

        private static (string address, string host) GetLocalInfo(Node node)
        {
            if (string.IsNullOrEmpty(node.Host))
            {
                return (string.Empty, string.Empty);
            }

            return ($"{node.Host}:{node.Port}", node.Host);
        }

        private static string GetRemoteAddress(ISession? session, Node node)
        {
            return session != null
                ? $"{session.RemoteHost}:{session.RemotePort}"
                : node.Address.ToString();
        }
    }
}
