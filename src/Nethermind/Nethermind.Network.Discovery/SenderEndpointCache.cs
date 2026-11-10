// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Resolves the socket addresses of received datagrams to sender endpoints, reusing the endpoint of a recent sender.
/// </summary>
/// <remarks>
/// Endpoints are normalized: an IPv4-mapped IPv6 sender (<c>::ffff:a.b.c.d</c>, reported by dual-stack sockets) is
/// reduced to its plain IPv4 form. Returned endpoints are shared between datagrams, so callers must not modify them.
/// The cache is cleared once it holds <paramref name="capacity"/> senders, which bounds its memory when senders do not
/// repeat. Not thread-safe; each receive loop owns its own instance.
/// </remarks>
internal sealed class SenderEndpointCache(int capacity)
{
    private static readonly IPEndPoint IPv4Template = new(IPAddress.Any, 0);
    private static readonly IPEndPoint IPv6Template = new(IPAddress.IPv6Any, 0);

    private readonly Dictionary<SocketAddress, IPEndPoint> _endpoints = new(capacity);

    /// <summary>
    /// Returns the normalized endpoint of <paramref name="address"/>, which may be overwritten after the call.
    /// </summary>
    public IPEndPoint GetOrAdd(SocketAddress address)
    {
        if (_endpoints.TryGetValue(address, out IPEndPoint? endpoint))
        {
            return endpoint;
        }

        if (_endpoints.Count >= capacity)
        {
            _endpoints.Clear();
        }

        IPEndPoint template = address.Family == AddressFamily.InterNetworkV6 ? IPv6Template : IPv4Template;
        endpoint = Normalize((IPEndPoint)template.Create(address));

        SocketAddress key = new(address.Family, address.Size);
        address.Buffer.Span[..address.Size].CopyTo(key.Buffer.Span);
        _endpoints.Add(key, endpoint);
        return endpoint;
    }

    private static IPEndPoint Normalize(IPEndPoint endpoint)
    {
        IPAddress address = endpoint.Address.NormalizeMappedIPv4();
        return ReferenceEquals(address, endpoint.Address) ? endpoint : new IPEndPoint(address, endpoint.Port);
    }
}
