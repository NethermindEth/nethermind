// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network;

/// <summary>Exemption lookup backed by the live static and trusted node managers.</summary>
public sealed class ManagedNodeExemptions(
    IStaticNodesManager staticNodesManager,
    ITrustedNodesManager trustedNodesManager) : IDiscoveryNodeExemptions
{
    /// <inheritdoc/>
    public bool IsExempt(PublicKey nodeId)
        => staticNodesManager.ContainsNode(nodeId) || trustedNodesManager.ContainsNode(nodeId);
}
