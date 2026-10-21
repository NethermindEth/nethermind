// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Network;

/// <summary>
/// Tells whether connections from a given IP address are privileged and must bypass connection rate-limiting.
/// </summary>
/// <remarks>
/// Used by <see cref="NodeFilter"/> for inbound connections, whose remote identity is unknown until the handshake
/// completes. Privileged addresses (static nodes) must always be accepted even though they cannot yet be matched
/// to a known node id.
/// </remarks>
public interface IPrivilegedIpProvider
{
    /// <summary>Returns <see langword="true"/> when <paramref name="ip"/> belongs to a privileged (static) node.</summary>
    bool IsPrivileged(IPAddress ip);
}
