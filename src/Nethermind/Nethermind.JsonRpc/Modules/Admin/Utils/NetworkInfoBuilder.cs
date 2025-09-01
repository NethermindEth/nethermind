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
            ArgumentNullException.ThrowIfNull(peer.Node);

            ISession? session = peer.InSession ?? peer.OutSession;
            (string localAddress, string localHost) = GetLocalInfo(peer.Node);
            string remoteAddress = GetRemoteAddress(session, peer.Node);

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

        private static (string address, string host) GetLocalInfo(Node node) =>
            string.IsNullOrEmpty(node.Host) ? (string.Empty, string.Empty) : ($"{node.Host}:{node.Port}", node.Host);

        private static string GetRemoteAddress(ISession? session, Node node) =>
            session is not null
                ? $"{session.RemoteHost}:{session.RemotePort}"
                : node.Address.ToString();
    }
}
