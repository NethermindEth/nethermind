// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Rlpx;

/// <summary>
/// Decides which peers an RLPx host contacts or accepts, and on which endpoints it dials them.
/// </summary>
/// <remarks>Shared by the RLPx hosts, so that each one owns only its transport.</remarks>
internal sealed class RlpxDialPolicy(NodeFilter nodeFilter, IPrivilegedIpProvider privilegedIpProvider, ILogger logger)
{
    /// <summary>Privileged addresses (static and trusted nodes) bypass the recent-IP filter so they can always connect.</summary>
    public bool ShouldContact(IPAddress ip, bool exactOnly = false)
        => privilegedIpProvider.IsPrivileged(ip) || nodeFilter.TryAccept(ip, exactOnly);

    /// <summary>
    /// Tells whether to accept an inbound connection, rejecting IPs already seen within the filter window.
    /// </summary>
    /// <remarks>
    /// Primary outgoing endpoints are recorded by <see cref="ShouldContact"/> before <see cref="ConnectAsync"/>;
    /// fallback endpoints are checked without mutation and recorded only after connecting.
    /// </remarks>
    public bool AcceptsInbound(IPAddress ip) => privilegedIpProvider.IsPrivileged(ip) || nodeFilter.TryAccept(ip);

    /// <summary>Refreshes the filter entry of a peer that sent or received a message.</summary>
    public void OnSessionActivity(Node remoteNode) => nodeFilter.Touch(remoteNode.Address.Address, remoteNode.IsStatic || remoteNode.IsBootnode);

    /// <summary>Dials a node on its endpoint, then on the other address family its verified ENR advertises.</summary>
    /// <param name="node">The node to dial.</param>
    /// <param name="tryConnect">Dials one endpoint and tells whether the connection was established.</param>
    /// <param name="cancellationToken">Cancels the dial, including the fallback.</param>
    public async Task<bool> ConnectAsync(Node node, Func<Node, CancellationToken, Task<bool>> tryConnect, CancellationToken cancellationToken)
    {
        if (logger.IsTrace) logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} initiating OUT connection");

        bool allowNonRoutable = node.Address.Address.IsLoopbackOrPrivateOrLinkLocal;
        bool primaryAccepted = IsDialAddressAcceptable(node.Address.Address, allowNonRoutable);
        if (!primaryAccepted)
        {
            if (logger.IsTrace) TraceRejectedDialEndpoint(node, node.Address);
        }
        else if (await tryConnect(node, cancellationToken))
        {
            return true;
        }

        if (!TryCreateAlternateDialNode(node, allowNonRoutable, out Node? alternate))
        {
            return false;
        }

        bool exactOnly = alternate.IsStatic || alternate.IsBootnode;
        bool privileged = privilegedIpProvider.IsPrivileged(alternate.Address.Address);
        if (!privileged && !nodeFilter.WouldAccept(alternate.Address.Address, exactOnly))
        {
            if (logger.IsTrace) TraceFilteredDialEndpoint(alternate);
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (logger.IsDebug)
        {
            string reason = primaryAccepted ? "Failed to connect" : "Rejected dial endpoint";
            logger.Debug($"{reason} for {node.ToString("s"):hide} on {node.Address:hide}, retrying on {alternate.Address:hide}");
        }

        bool connected = await tryConnect(alternate, cancellationToken);
        if (connected && !privileged)
        {
            nodeFilter.Touch(alternate.Address.Address, exactOnly);
        }

        return connected;
    }

    internal static bool TryCreateAlternateDialNode(Node node, bool allowNonRoutable, [NotNullWhen(true)] out Node? alternate)
    {
        NodeRecord? record = node.Enr;
        AddressFamily alternateFamily = node.Address.AddressFamily switch
        {
            AddressFamily.InterNetwork => AddressFamily.InterNetworkV6,
            AddressFamily.InterNetworkV6 => AddressFamily.InterNetwork,
            _ => AddressFamily.Unknown
        };
        bool familySupported = alternateFamily switch
        {
            AddressFamily.InterNetwork => Socket.OSSupportsIPv4,
            AddressFamily.InterNetworkV6 => Socket.OSSupportsIPv6,
            _ => false
        };
        if (!familySupported || record is null || !node.IsVerifiedEnr(record) ||
            record.EnrSequence < node.HighestObservedEnrSequence ||
            !Node.TryFromEnr(record, alternateFamily, out alternate) ||
            alternate.Id != node.Id || alternate.Address.Equals(node.Address) ||
            (IPAddress.IsLoopback(alternate.Address.Address) && !IPAddress.IsLoopback(node.Address.Address)) ||
            !IsDialAddressAcceptable(alternate.Address.Address, allowNonRoutable))
        {
            alternate = null;
            return false;
        }

        alternate.MergeEnrStateFrom(node);
        alternate.IsStatic = node.IsStatic;
        alternate.IsTrusted = node.IsTrusted;
        alternate.IsBootnode = node.IsBootnode;
        return true;
    }

    internal static bool IsDialAddressAcceptable(IPAddress address, bool allowNonRoutable)
    {
        ParsedIPAddress parsed = ParsedIPAddress.Parse(address);
        return !parsed.IsWildcardOrNone &&
               !parsed.IsMulticast &&
               !parsed.IsSpecialUseAddress &&
               (allowNonRoutable || !parsed.IsLoopbackOrPrivateOrLinkLocal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceRejectedDialEndpoint(Node node, IPEndPoint endpoint) =>
        logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} rejected OUT endpoint {endpoint:hide}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceFilteredDialEndpoint(Node node) =>
        logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} rejected filtered OUT endpoint {node.Address:hide}");
}
