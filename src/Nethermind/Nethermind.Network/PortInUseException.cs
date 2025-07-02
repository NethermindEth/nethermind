// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Config;

namespace Nethermind.Network;

public class PortInUseException : IOException
{
    public PortInUseException(Exception exception, params int[] ports) : base(
        $"{GetReason(ports)} " +
        "If you intend to run 2 or more nodes on one machine, ensure you have changed all configured ports under: " +
        $"{"\n\t" + string.Join("\n\t", ConfigExtensions.GetPortOptionNames())}",
        exception
    )
    { }

    public PortInUseException(Exception exception, params string[] urls) : this(exception, GetPorts(urls)) { }

    private static int[] GetPorts(string[] urls) => urls.Select(static u => new Uri(u).Port).ToArray();

    private static string GetReason(params int[] ports)
    {
        return ports.Length switch
        {
            0 => "One of the configured ports is in use.",
            1 => $"Port {ports[0]} is in use.",
            _ => $"One or more of the ports {string.Join(',', ports)} are in use."
        };
    }
}
