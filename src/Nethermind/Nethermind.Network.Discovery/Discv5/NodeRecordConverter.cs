// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5;

internal static class NodeRecordConverter
{
    public static bool TryGetNodeFromEnr(NodeRecord enr, bool allowNonRoutable, [NotNullWhen(true)] out Node? node)
    {
        node = null;

        PublicKey? key = enr.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress();
        (IPAddress? ip, int? discoveryPort) = GetDiscoveryEndpoint(enr);
        if (key is null || ip is null || discoveryPort is null)
        {
            return false;
        }

        if (!DiscoveryV5App.IsDiscoveryAddressAcceptable(ip, allowNonRoutable))
        {
            return false;
        }

        if ((uint)discoveryPort.Value > ushort.MaxValue || discoveryPort.Value == 0)
        {
            return false;
        }

        node = new Node(key, ip.ToString(), discoveryPort.Value)
        {
            Enr = enr.EnrString
        };
        return true;
    }

    internal static (IPAddress? Ip, int? Port) GetDiscoveryEndpoint(NodeRecord enr)
    {
        IPAddress? ip = enr.GetObj<IPAddress>(EnrContentKey.Ip);
        int? udp = enr.GetValue<int>(EnrContentKey.Udp);
        if (ip is not null && udp is not null)
        {
            return (ip, udp);
        }

        IPAddress? ip6 = enr.GetObj<IPAddress>(EnrContentKey.Ip6);
        int? udp6 = enr.GetValue<int>(EnrContentKey.Udp6) ?? udp;
        return ip6 is not null && udp6 is not null ? (ip6, udp6) : (null, null);
    }
}
