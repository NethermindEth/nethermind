// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network;

/// <summary>
/// Live lookup for nodes exempt from discovery subnet limits (managed static/trusted entries).
/// Implemented by the full client; absent in slim hosts such as the bootnode.
/// </summary>
public interface IDiscoveryNodeExemptions
{
    /// <summary>Returns <see langword="true"/> when a managed static or trusted node has <paramref name="nodeId"/> as its identity.</summary>
    bool IsExempt(PublicKey nodeId);
}
