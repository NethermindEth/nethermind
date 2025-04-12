// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Nethermind.Network;

public static class NetworkHelper
{
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
