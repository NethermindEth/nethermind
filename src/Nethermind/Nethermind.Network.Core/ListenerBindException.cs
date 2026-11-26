// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;

namespace Nethermind.Network;

/// <summary>Thrown when a network listener cannot bind its configured address and port.</summary>
public class ListenerBindException : IOException
{
    public ListenerBindException(Exception exception, params int[] ports) : this(
        $"Unable to bind {GetPortsText(ports)}: {exception.Message.TrimEnd('.')}. " +
            "Check that the configured listen address (Network.LocalIp for P2P and discovery, JsonRpc.Host for JSON-RPC) " +
            "is assigned to a local network interface.",
        exception)
    {
    }

    public ListenerBindException(Exception exception, params string[] urls) : this(exception, GetPorts(urls)) { }

    protected ListenerBindException(string message, Exception exception) : base(message, exception) { }

    protected static int[] GetPorts(string[] urls) => urls.Select(static u => new Uri(u).Port).ToArray();

    private static string GetPortsText(int[] ports) => ports.Length switch
    {
        0 => "the configured ports",
        1 => $"port {ports[0]}",
        _ => $"ports {string.Join(',', ports)}"
    };
}
