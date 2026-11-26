// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Discovery;

namespace Nethermind.Network.Rlpx;

/// <summary>
/// Binds the listener of an RLPx host.
/// </summary>
/// <remarks>Shared by the RLPx hosts, so that each one owns only its transport.</remarks>
internal static class RlpxListener
{
    /// <summary>Binds on the preferred address, or on the fallback address if that fails.</summary>
    /// <param name="listenerState">The addresses to bind on.</param>
    /// <param name="port">The port to bind on, for the log.</param>
    /// <param name="logger">The host's logger.</param>
    /// <param name="hostName">The host's name, for the log.</param>
    /// <param name="bind">Binds the listener on an address.</param>
    public static async Task<T> BindAsync<T>(NetworkListenerState listenerState, int port, ILogger logger, string hostName, Func<IPAddress, Task<T>> bind)
    {
        IPAddress bindAddress = listenerState.PreferredAddress;
        try
        {
            return await bind(bindAddress);
        }
        catch (Exception e) when (!bindAddress.Equals(listenerState.FallbackAddress))
        {
            if (logger.IsWarn) logger.Warn($"Failed to bind {hostName} on {bindAddress:hide}:{port:hide}. Retrying on {listenerState.FallbackAddress:hide}:{port:hide}. {e:hide}");
            return await bind(listenerState.FallbackAddress);
        }
    }

    /// <summary>Creates a listener socket for an address, dual mode when the address and the host allow it.</summary>
    public static Socket CreateSocket(IPAddress address)
    {
        Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = DiscoveryAddressSupport.SupportsFamily(address, AddressFamily.InterNetwork);
                if (socket.DualMode)
                {
                    // A dual-mode bind must not share its IPv4 port, or the advertised capability is ambiguous.
                    socket.ExclusiveAddressUse = true;
                }
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
