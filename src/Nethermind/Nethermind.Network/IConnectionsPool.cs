// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;

namespace Nethermind.Network;

public interface IConnectionsPool
{
    /// <summary>Binds the discovery listener to the shared preferred address, falling back to its resolved address when needed.</summary>
    /// <param name="bootstrapFactory">Creates the transport bootstrap for each bind attempt.</param>
    /// <param name="channelFactory">Creates a datagram channel for an address family.</param>
    /// <param name="port">The local UDP port to bind.</param>
    /// <returns>The successfully bound channel.</returns>
    public Task<IChannel> BindAsync(
        Func<Bootstrap> bootstrapFactory,
        Func<IPAddress, IChannel> channelFactory,
        int port);
    public Task StopAsync();
}
