// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>rollup0's derived header: the parent's timestamp plus the block time, a zero prevRandao, the configured gas limit.</summary>
public sealed class Rollup0HeaderRules(ISpecProvider specProvider, EezSettlementContext context) : IDerivedHeaderRules
{
    /// <param name="prevRandao">Always zero under rollup0; see <see cref="Seed"/>.</param>
    public (BlockHeader Header, IReleaseSpec Spec) Build(BlockHeader parent, Address beneficiary, byte[] extraData, Hash256 prevRandao)
    {
        IReleaseSpec spec = specProvider.GetSpec(parent.Number + 1, DerivedHeader.Timestamp(parent, context));
        return (DerivedHeader.Build(parent, spec, context, beneficiary, extraData), spec);
    }

    public Withdrawal[]? Withdrawals(IReleaseSpec spec) => DerivedHeader.Withdrawals(spec);

    public string? Mismatch(Block block, BlockHeader parent) => DerivedHeader.Mismatch(block, parent, specProvider.GetSpec(block.Header), context);

    /// <summary>rollup0 blocks carry no randomness: their prevRandao is zero.</summary>
    public Hash256 Seed(BlockHeader endpoint, L1Anchor? settling, Hash256? previousSeed = null) => Keccak.Zero;
}
