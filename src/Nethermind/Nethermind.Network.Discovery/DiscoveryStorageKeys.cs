// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

/// <summary>
/// Keys of the <see cref="INetworkStorage"/> instances that persist the nodes each discovery protocol found.
/// </summary>
/// <remarks>
/// The full client registers each storage under the name of its database, so these equal <c>DbNames.DiscoveryNodes</c>
/// and <c>DbNames.DiscoveryV5Nodes</c>.
/// </remarks>
public static class DiscoveryStorageKeys
{
    public const string V4 = "discoveryNodes";
    public const string V5 = "discoveryV5Nodes";
}
