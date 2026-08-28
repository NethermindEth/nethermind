// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

internal static class IPAddressRlp
{
    public static int GetLength(IPAddress ip)
        => ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => Rlp.LengthOfByteString(4, 0),
            AddressFamily.InterNetworkV6 => Rlp.LengthOfByteString(16, 0),
            _ => Rlp.LengthOf(ip.GetAddressBytes())
        };

    [SkipLocalsInit]
    public static void Encode<TWriter>(ref TWriter writer, IPAddress ip)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        Span<byte> bytes = stackalloc byte[16];
        if (ip.TryWriteBytes(bytes, out int bytesWritten))
        {
            writer.Encode(bytes[..bytesWritten]);
            return;
        }

        writer.Encode(ip.GetAddressBytes());
    }
}
