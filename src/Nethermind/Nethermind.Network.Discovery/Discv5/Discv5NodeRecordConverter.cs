// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5;

internal static class Discv5NodeRecordConverter
{
    public static bool TryGetNodeFromEnr(NodeRecord enr, bool allowNonRoutable, [NotNullWhen(true)] out Node? node)
    {
        node = null;

        PublicKey? key = enr.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress();
        IPAddress? ip = enr.GetObj<IPAddress>(EnrContentKey.Ip);
        int? discoveryPort = enr.GetValue<int>(EnrContentKey.Udp) ?? enr.GetValue<int>(EnrContentKey.Tcp);
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
}
