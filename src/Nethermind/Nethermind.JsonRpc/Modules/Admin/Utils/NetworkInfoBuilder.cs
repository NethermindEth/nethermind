// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P;
using Nethermind.Network;
using Nethermind.Stats.Model;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class NetworkInfoBuilder
    {
        public static NetworkInfo Build(Peer peer, bool isInbound, NodeInfo nodeInfo = null)
        {
            if (peer?.Node == null)
            {
                throw new ArgumentNullException(nameof(peer));
            }

            var session = peer.InSession ?? peer.OutSession;

            var localAddress = GetLocalAddress(session, nodeInfo);
            var remoteAddress = GetRemoteAddress(session, peer.Node);

            return new NetworkInfo
            {
                LocalAddress = localAddress,
                RemoteAddress = remoteAddress,
                Inbound = isInbound,
                Trusted = peer.Node.IsTrusted,
                Static = peer.Node.IsStatic
            };
        }

        private static string GetLocalAddress(ISession? session, NodeInfo? nodeInfo)
        {
            if (session != null && !string.IsNullOrEmpty(nodeInfo?.ListenAddress))
            {
                return nodeInfo.ListenAddress;
            }

            return string.Empty;
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
