// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Nethermind.Stats.Model;

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

    /// <summary>Marks a matching node as static and tracks it, so <see cref="Demote"/> can undo it.</summary>
    /// <param name="node">The node to promote. A node that is already static is not changed.</param>
    /// <param name="matchPort"><c>false</c> to match the IP only.</param>
    /// <returns><c>true</c> when the node was promoted.</returns>
    /// <remarks>A promoted node is demoted when a hostname refresh drops its IP.</remarks>
    bool TryPromote(Node node, bool matchPort);

    /// <summary>Clears the static flag set by <see cref="TryPromote"/>. Nodes that are static for other reasons are not changed.</summary>
    void Demote(Node node);

    /// <summary>Raised when a hostname resolves to a new set of addresses.</summary>
    event EventHandler? Changed;
}
