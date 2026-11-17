// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Nethermind.Config;
using Nethermind.Network.P2P;
using Nethermind.Network;
using Nethermind.Stats.Model;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class NetworkInfoBuilder
    {
        public static NetworkInfo Build(Peer peer, bool isInbound, IPAddress? localIp = null)
        {
            ArgumentNullException.ThrowIfNull(peer.Node);

            ISession? session = peer.InSession ?? peer.OutSession;
            string localAddress = GetLocalAddress(session, localIp);
            string remoteAddress = GetRemoteAddress(session, peer.Node);

            return new NetworkInfo
            {
                LocalAddress = localAddress,
                LocalHost = peer.Node.Host,
                RemoteAddress = remoteAddress,
                Inbound = isInbound,
                Trusted = peer.Node.IsTrusted,
                Static = peer.Node.IsStatic
            };
        }

        private static string GetLocalAddress(ISession? session, IPAddress? localIp) =>
            session is null || localIp is null ? string.Empty : $"{Enode.FormatEnodeHost(localIp)}:{session.LocalPort}";

        private static string GetRemoteAddress(ISession? session, Node node) =>
            session is not null
                ? $"{session.RemoteHost}:{session.RemotePort}"
                : node.Address.ToString();
    }
}
