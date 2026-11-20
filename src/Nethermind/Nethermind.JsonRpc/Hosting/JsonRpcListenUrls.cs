// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Hosting;

/// <summary>Resolves JSON-RPC listener addresses without implicit wildcard host-name binding.</summary>
public static class JsonRpcListenUrls
{
    /// <summary>
    /// Builds the addresses Kestrel binds to, replacing each host name with the IP addresses it resolves to.
    /// </summary>
    /// <remarks>
    /// Kestrel binds any host that is neither an IP literal nor <c>localhost</c> to all interfaces,
    /// so a host name such as <c>node.lan</c> would otherwise expose the port on every address of the machine.
    /// </remarks>
    /// <param name="urls">Configured JSON-RPC listeners.</param>
    /// <param name="resolveHost">Resolves a host name to its bind addresses.</param>
    /// <param name="cancellationToken">Cancels host resolution.</param>
    /// <returns>The explicit listener addresses.</returns>
    /// <exception cref="InvalidOperationException">
    /// A host name cannot be resolved, resolves to no addresses, or resolves to an unspecified address
    /// (<c>0.0.0.0</c>, <c>::</c> or <c>::ffff:0.0.0.0</c>), which Kestrel would bind to all interfaces.
    /// </exception>
    public static async Task<string[]> ResolveAsync(
        IEnumerable<JsonRpcUrl> urls,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveHost,
        CancellationToken cancellationToken)
    {
        List<string> listenUrls = [];
        foreach (JsonRpcUrl url in urls)
        {
            if (IPAddress.TryParse(url.Host, out _) ||
                url.Host is "*" or "+" ||
                string.Equals(url.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                listenUrls.Add(url.ToString());
                continue;
            }

            IPAddress[] addresses;
            try
            {
                addresses = await resolveHost(url.Host, cancellationToken);
            }
            catch (SocketException e)
            {
                throw UnresolvedHost(url.Host, e);
            }

            if (addresses.Length == 0)
            {
                throw UnresolvedHost(url.Host, null);
            }

            foreach (IPAddress address in addresses)
            {
                IPAddress unmappedAddress = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
                if (unmappedAddress.Equals(IPAddress.Any) || unmappedAddress.Equals(IPAddress.IPv6Any))
                {
                    throw new InvalidOperationException($"JSON RPC host '{url.Host}' resolves to the unspecified address {address}. Use 0.0.0.0 directly to listen on all interfaces.");
                }

                string host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
                string listenUrl = $"{url.Scheme}://{host}:{url.Port}";
                if (!listenUrls.Contains(listenUrl))
                {
                    listenUrls.Add(listenUrl);
                }
            }
        }

        return listenUrls.ToArray();

        static InvalidOperationException UnresolvedHost(string host, Exception? innerException) =>
            new($"Failed to resolve JSON RPC host '{host}'. Use an IP address, or 0.0.0.0 to listen on all interfaces.", innerException);
    }
}
