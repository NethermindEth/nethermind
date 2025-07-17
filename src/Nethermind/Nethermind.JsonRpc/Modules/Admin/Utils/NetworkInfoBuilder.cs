// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class NetworkInfoBuilder
    {
        public static NetworkInfo Build(Peer peer, bool isInbound, NodeInfo nodeInfo = null)
        {
            string localAddress = string.Empty;
            string remoteAddress = peer.Node.Address.ToString();

            ISession? session = peer.InSession ?? peer.OutSession;
            if (session != null)
            {
                if (!string.IsNullOrEmpty(nodeInfo?.ListenAddress))
                {
                    localAddress = nodeInfo.ListenAddress;
                }
                
                remoteAddress = $"{session.RemoteHost}:{session.RemotePort}";
            }

            return new NetworkInfo
            {
                LocalAddress = localAddress,
                RemoteAddress = remoteAddress,
                Inbound = isInbound,
                Trusted = peer.Node.IsTrusted,
                Static = peer.Node.IsStatic
            };
        }
    }
}