// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;

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
}
