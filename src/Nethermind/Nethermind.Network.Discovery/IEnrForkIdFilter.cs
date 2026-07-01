// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery;

public interface IEnrForkIdFilter
{
    /// <summary>
    /// Returns whether the ENR advertises an execution fork ID compatible with the local chain.
    /// </summary>
    /// <remarks>
    /// Records without the <c>eth</c> entry, malformed fork hashes, or incompatible fork IDs are rejected.
    /// </remarks>
    bool IsAcceptable(NodeRecord record);
}
