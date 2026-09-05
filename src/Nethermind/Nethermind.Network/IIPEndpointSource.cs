// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Logging;

namespace Nethermind.Network;

public interface IIPEndpointSource
{
    public IPEndPoint IPEndpoint { get; }
}

public static class EndpointExtensions
{
    public static IPEndPoint ToIPEndpoint(this EndPoint endpoint)
    {
        if (endpoint is IPEndPoint ipEndPoint) return ipEndPoint;
        if (endpoint is IIPEndpointSource source) return source.IPEndpoint;
        throw new InvalidOperationException($"{endpoint} cannot be converted to IPEndpoint.");
    }

    internal static IPEndPoint? TryGetLocalIPEndpoint(this IChannel channel)
        => channel.LocalAddress switch
        {
            IPEndPoint ipEndpoint => ipEndpoint,
            IIPEndpointSource source => source.IPEndpoint,
            _ => (channel as IIPEndpointSource)?.IPEndpoint
        };

    internal static async Task CloseFailedBindAsync(this IChannel? channel, ILogger logger, string listenerName)
    {
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.CloseAsync();
        }
        catch (Exception e)
        {
            if (logger.IsWarn) logger.Warn($"Failed to close an unsuccessful {listenerName} bind attempt. {e}");
        }
    }
}
