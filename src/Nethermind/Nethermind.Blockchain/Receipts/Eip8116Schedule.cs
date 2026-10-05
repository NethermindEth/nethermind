// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Receipts;

/// <summary>Tells whether EIP-8116 receipt semantics apply to a block.</summary>
/// <remarks>
/// On a chain that never schedules EIP-8116 no spec is looked up per block, keeping per-block log scans as cheap
/// as they were before the EIP. A <see langword="null"/> provider means EIP-8116 never applies.
/// </remarks>
public readonly struct Eip8116Schedule(ISpecProvider? specProvider)
{
    private readonly ISpecProvider? _specProvider = specProvider?.GetFinalSpec().IsEip8116Enabled == true ? specProvider : null;

    /// <summary>Whether the block at <paramref name="blockNumber"/> and <paramref name="timestamp"/> follows EIP-8116.</summary>
    public bool IsEnabled(ulong blockNumber, ulong timestamp) =>
        _specProvider is not null && _specProvider.GetSpec(blockNumber, timestamp).IsEip8116Enabled;
}
