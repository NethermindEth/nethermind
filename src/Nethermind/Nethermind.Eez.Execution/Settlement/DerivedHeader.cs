// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The header fields derivation computes instead of reading them from DA: a follower rebuilds every block from its
/// parent, the DA's beneficiary and extra data, and fixed rollup parameters. A block whose header differs settles a
/// hash no follower can derive, so it must not be attested.
/// </summary>
/// <remarks>The block validator already ties the base fee, blob gas, number and parent hash to the parent.</remarks>
public static class DerivedHeader
{
    /// <exception cref="EezSettlementException">The header differs from the one derivation builds.</exception>
    public static void Ensure(Block block, BlockHeader parent, IReleaseSpec spec, EezSettlementContext context)
    {
        BlockHeader header = block.Header;
        ulong timestamp = parent.Timestamp > ulong.MaxValue - context.BlockTimeSeconds ? ulong.MaxValue : parent.Timestamp + context.BlockTimeSeconds;
        Require(header.Timestamp == timestamp, header, $"timestamp {header.Timestamp}, not {timestamp}");
        Require(header.GasLimit == context.GasLimit, header, $"gas limit {header.GasLimit}, not {context.GasLimit}");
        Require(header.MixHash == Keccak.Zero, header, "a non-zero prevRandao");
        Require(spec.IsBeaconBlockRootAvailable ? header.ParentBeaconBlockRoot == Keccak.Zero : header.ParentBeaconBlockRoot is null, header,
            "a parent beacon block root other than the fork's zero root");
        Require(spec.WithdrawalsEnabled ? block.Withdrawals is { Length: 0 } : block.Withdrawals is null, header, "withdrawals other than the fork's empty list");
        Require(header.Difficulty.IsZero && header.Nonce == 0 && header.UnclesHash == Keccak.OfAnEmptySequenceRlp, header, "proof-of-work fields");
    }

    private static void Require(bool condition, BlockHeader header, string field)
    {
        if (!condition)
        {
            throw new EezSettlementException($"Block {header.Number} has {field}, so derivation cannot rebuild it.");
        }
    }
}
