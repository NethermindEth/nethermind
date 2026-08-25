// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Nethermind.Network;

public static class NetworkHelper
{
    /// <summary>
    /// Selects the address to bind inbound listeners to.
    /// </summary>
    /// <remarks>
    /// A wildcard local IP is upgraded to <see cref="IPAddress.IPv6Any"/> so the listener socket accepts both
    /// address families (the DotNetty sockets are created dual-mode). Callers must be prepared for the bind to
    /// fail and retry with the original address. Specific addresses are returned unchanged so an operator can
    /// pin the listener to a single family or interface. An explicit <c>0.0.0.0</c> override is kept IPv4-only;
    /// only an unset wildcard is widened. macOS is excluded uniformly as a conservative measure:
    /// its discovery datagram channel is deliberately created with
    /// <see cref="System.Net.Sockets.AddressFamily.InterNetwork"/> (see <c>CompositeDiscoveryApp</c>).
    /// </remarks>
    public static IPAddress GetInboundBindAddress(IPAddress localIp)
        => GetInboundBindAddress(localIp, localIpConfig: null, CanBindDualStack());

    public static IPAddress GetInboundBindAddress(IPAddress localIp, string? localIpConfig)
        => GetInboundBindAddress(localIp, localIpConfig, CanBindDualStack());

    internal static IPAddress GetInboundBindAddress(IPAddress localIp, bool supportsDualStack)
        => GetInboundBindAddress(localIp, localIpConfig: null, supportsDualStack);

    internal static IPAddress GetInboundBindAddress(IPAddress localIp, string? localIpConfig, bool supportsDualStack)
        => supportsDualStack && IsWildcardForDualStack(localIp, localIpConfig) ? IPAddress.IPv6Any : localIp;

    private static bool IsWildcard(IPAddress localIp)
        => IPAddress.Any.Equals(localIp) || IPAddress.IPv6Any.Equals(localIp);

    private static bool IsWildcardForDualStack(IPAddress localIp, string? localIpConfig)
    {
        if (!IsWildcard(localIp)) return false;
        // Explicit IPv4 wildcard must stay IPv4-only; only the default (unset) wildcard is widened.
        if (localIpConfig is not null && localIpConfig.Trim() == IPAddress.Any.ToString())
        {
            return false;
        }

        return true;
    }

    private static bool CanBindDualStack()
        => !RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && Socket.OSSupportsIPv6;

    /// <summary>
    /// Reduces an IPv4-mapped IPv6 address (<c>::ffff:a.b.c.d</c>) to its plain IPv4 form; any other address is returned unchanged.
    /// </summary>
    public static IPAddress NormalizeIpv4Mapped(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static PortInUseException MapOrRethrow(Exception exception, int[]? ports = null, string[]? urls = null)
    {
        if (exception is AggregateException)
            exception = exception.InnerException!;

        switch (exception)
        {
            case SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }:
                return ports != null ? new(exception, ports) : new(exception, urls!);
            case IOException { Source: "Grpc.Core" } when exception.Message.Contains("Failed to bind port"):
                return ports != null ? new(exception, ports) : new(exception, urls!);
            default:
                ExceptionDispatchInfo.Throw(exception);
                throw exception; // Make compiler happy, should never execute
        }
    }

    public static void HandlePortTakenError(Action action, params int[] ports)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            throw MapOrRethrow(exception, ports: ports);
        }
    }

    public static T HandlePortTakenError<T>(Func<T> action, params int[] ports)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            throw MapOrRethrow(exception, ports: ports);
        }
    }

    public static async Task HandlePortTakenError(Func<Task> action, params string[] urls)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            throw MapOrRethrow(exception, urls: urls);
        }
    }

    public static async Task<T> HandlePortTakenError<T>(Func<Task<T>> action, params int[] ports)
    {
        try
        {
            return await action();
        }
        catch (Exception exception)
        {
            throw MapOrRethrow(exception, ports: ports);
        }
    }
}
