// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using Nethermind.Config;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

internal static class PersistedPeerPolicy
{
    internal const int MaxPeersPerAddress = 16;

    internal static bool TryGetAddress(NetworkNode node, out IPAddress address, PeerAddressPermissions allowNonRoutable)
    {
        address = node.HostIp.NormalizeMappedIPv4();
        return node.Port is > 0 and <= ushort.MaxValue && PeerAddressPolicy.IsAcceptable(address, allowNonRoutable);
    }

    internal static bool TryReserveAddress(Dictionary<IPAddress, int> counts, IPAddress address)
    {
        int count = counts.GetValueOrDefault(address);
        if (count >= MaxPeersPerAddress) return false;
        counts[address] = count + 1;
        return true;
    }

    internal static NetworkNode CreateNode(Node node, IPEndPoint connectedEndpoint, long reputation)
    {
        NodeRecord? record = node.Enr;
        if (record is not null && node.IsVerifiedEnr(record) && record.EnrSequence >= node.HighestObservedEnrSequence &&
            record.TryGetTcpEndpoint(out IPEndPoint? tcpEndpoint) &&
            tcpEndpoint.Port == connectedEndpoint.Port &&
            tcpEndpoint.Address.NormalizeMappedIPv4().Equals(connectedEndpoint.Address))
        {
            return new NetworkNode(record.ToString()) { Reputation = reputation };
        }

        return new NetworkNode(node.Id, connectedEndpoint.Address.ToString(), connectedEndpoint.Port, reputation);
    }
}
