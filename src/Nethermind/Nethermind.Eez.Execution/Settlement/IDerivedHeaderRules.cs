// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The header fields an EEZ chain derives for a block instead of reading them from DA. A follower rebuilds every block
/// with them, the sequencer builds with them, and a block whose header differs is never kept as a derived block.
/// </summary>
public interface IDerivedHeaderRules
{
    /// <summary>The child of <paramref name="parent"/> before execution fills in its roots and gas, and the spec it runs under.</summary>
    (BlockHeader Header, IReleaseSpec Spec) Build(BlockHeader parent, Address beneficiary, byte[] extraData, Hash256 prevRandao);

    /// <summary>The withdrawals every derived block carries under <paramref name="spec"/>.</summary>
    Withdrawal[]? Withdrawals(IReleaseSpec spec);

    /// <returns>The first header field of <paramref name="block"/> that differs from the one derivation builds, or <see langword="null"/>.</returns>
    string? Mismatch(Block block, BlockHeader parent);

    /// <summary>The prevRandao of the blocks after a settled endpoint, until the next one.</summary>
    /// <param name="settling">The L1 block that settled <paramref name="endpoint"/>; <see langword="null"/> for genesis.</param>
    Hash256 Seed(BlockHeader endpoint, L1Anchor? settling, Hash256? previousSeed = null);
}

/// <summary>The L1 block a settlement landed in, as far as L2 derivation reads it.</summary>
public readonly record struct L1Anchor(ulong Number, Hash256 Hash, ulong Timestamp, Hash256 MixHash)
{
    public Hash256? ParentHash { get; init; }
}
