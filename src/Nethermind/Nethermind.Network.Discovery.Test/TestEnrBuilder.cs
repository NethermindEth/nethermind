// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Test;

internal static class TestEnrBuilder
{
    public static NodeRecord BuildSigned(
        PrivateKey privateKey,
        IPAddress? ipAddress = null,
        int? tcpPort = 30303,
        int? udpPort = 30303,
        bool useUdp6 = true,
        ulong enrSequence = 1,
        Action<NodeRecord>? configureExtras = null)
    {
        IPAddress ip = ipAddress ?? IPAddress.Loopback;
        bool isIpv6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        NodeRecord enr = new();
        if (isIpv6)
        {
            enr.SetEntry(new Ip6Entry(ip));
        }
        else
        {
            enr.SetEntry(new IpEntry(ip));
        }
        enr.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
        if (tcpPort is { } tcp)
        {
            enr.SetEntry(new TcpEntry(tcp));
        }
        if (udpPort is { } udp)
        {
            if (isIpv6 && useUdp6)
            {
                enr.SetEntry(new Udp6Entry(udp));
            }
            else
            {
                enr.SetEntry(new UdpEntry(udp));
            }
        }
        configureExtras?.Invoke(enr);
        enr.EnrSequence = enrSequence;
        Sign(enr, privateKey);
        return enr;
    }

    public static NodeRecord BuildSignedWithoutEndpoint(
        PrivateKey privateKey,
        ulong enrSequence = 1,
        Action<NodeRecord>? configureExtras = null)
    {
        NodeRecord enr = new();
        enr.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
        configureExtras?.Invoke(enr);
        enr.EnrSequence = enrSequence;
        Sign(enr, privateKey);
        return enr;
    }

    public static bool HasEnrSequence(Node node, ulong enrSequence)
    {
        if (string.IsNullOrEmpty(node.Enr))
        {
            return false;
        }

        try
        {
            return NodeRecord.FromEnrString(node.Enr).EnrSequence == enrSequence;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Sign(NodeRecord enr, PrivateKey privateKey) => new NodeRecordSigner(new EthereumEcdsa(0), privateKey).Sign(enr);
}

internal sealed class TestEth2Entry() : EnrContentEntry<byte[]>([1, 2, 3, 4])
{
    public override string Key => EnrContentKey.Eth2;

    protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value);

    protected override void EncodeValue(RlpStream rlpStream) => rlpStream.Encode(Value);
}
