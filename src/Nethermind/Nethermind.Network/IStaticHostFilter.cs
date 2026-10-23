// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;

namespace Nethermind.Network;

/// <summary>
/// Matches node addresses against the <c>Network.StaticPeerHosts</c> config. A matching node is treated as a static peer.
/// </summary>
public interface IStaticHostFilter
{
    /// <summary>Matches the IP only. Used for inbound connections, where the remote listen port is unknown.</summary>
    bool IsMatch(IPAddress address);

    /// <summary>Matches the IP, and the TCP port when the entry has a port.</summary>
    bool IsMatch(IPEndPoint endPoint);

    /// <summary>Raised when a hostname resolves to a new set of addresses.</summary>
    event EventHandler? Changed;
}
