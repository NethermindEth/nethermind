// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Nethermind.Network;

public static class NetworkHelper
{
    /// <summary>
    /// Selects the address to bind inbound listeners to.
    /// </summary>
    /// <remarks>
    /// On supported platforms, a wildcard local IP is upgraded to <see cref="IPAddress.IPv6Any"/> so the listener
    /// socket accepts both address families (the DotNetty sockets are created dual-mode). Callers must be prepared
    /// for the bind to fail and retry with the original address. Automatic widening is disabled on macOS; an explicit
    /// <see cref="IPAddress.IPv6Any"/> remains dual-mode. Specific addresses are returned unchanged so an operator can
    /// pin the listener to a single family or interface. An explicit <c>0.0.0.0</c> override is kept IPv4-only; only an
    /// unset wildcard is widened.
    /// </remarks>
    internal static IPAddress GetInboundBindAddress(IPAddress localIp, string? localIpConfig)
        => GetInboundBindAddress(
            localIp,
            localIpConfig,
            Socket.OSSupportsIPv6 && !OperatingSystem.IsMacOS());

    internal static IPAddress GetInboundBindAddress(IPAddress localIp, string? localIpConfig, bool supportsDualStack)
        => supportsDualStack && localIpConfig is null && IPAddress.Any.Equals(localIp)
            ? IPAddress.IPv6Any
            : localIp;

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
