// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The header fields derivation computes instead of reading them from DA: a follower rebuilds every block from its
/// parent, the DA's beneficiary and extra data, and fixed rollup parameters. A block whose header differs settles a
/// hash no follower can derive, so it must not be attested. The follower builds from <see cref="Build"/> and the
/// attester checks against it, so both apply the same rules.
/// </summary>
/// <remarks>The block validator already ties the base fee, blob gas, number and parent hash to the parent.</remarks>
public static class DerivedHeader
{
    /// <summary>The header derivation builds on <paramref name="parent"/>, before execution fills in its roots and gas.</summary>
    public static BlockHeader Build(BlockHeader parent, IReleaseSpec spec, EezSettlementContext context, Address beneficiary, byte[] extraData) =>
        new(parent.Hash!, Keccak.OfAnEmptySequenceRlp, beneficiary, UInt256.Zero, parent.Number + 1, context.GasLimit, Timestamp(parent, context), extraData)
        {
            MixHash = Keccak.Zero,
            Nonce = 0,
            ParentBeaconBlockRoot = spec.IsBeaconBlockRootAvailable ? Keccak.Zero : null,
            BaseFeePerGas = spec.IsEip1559Enabled ? BaseFeeCalculator.Calculate(parent, spec) : UInt256.Zero,
            ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec),
        };

    /// <summary>The withdrawals every derived block carries: none, as an empty list once the fork has them.</summary>
    public static Withdrawal[]? Withdrawals(IReleaseSpec spec) => spec.WithdrawalsEnabled ? [] : null;

    /// <exception cref="EezSettlementException">The header differs from the one derivation builds.</exception>
    public static void Ensure(Block block, BlockHeader parent, IReleaseSpec spec, EezSettlementContext context)
    {
        BlockHeader header = block.Header;
        BlockHeader derived = Build(parent, spec, context, header.Beneficiary!, header.ExtraData);
        Require(header.Timestamp == derived.Timestamp, header, $"timestamp {header.Timestamp}, not {derived.Timestamp}");
        Require(header.GasLimit == derived.GasLimit, header, $"gas limit {header.GasLimit}, not {derived.GasLimit}");
        Require(header.MixHash == derived.MixHash, header, "a non-zero prevRandao");
        Require(header.ParentBeaconBlockRoot == derived.ParentBeaconBlockRoot, header, "a parent beacon block root other than the fork's zero root");
        Require(Withdrawals(spec) is null ? block.Withdrawals is null : block.Withdrawals is { Length: 0 }, header, "withdrawals other than the fork's empty list");
        Require(header.Difficulty == derived.Difficulty && header.Nonce == derived.Nonce && header.UnclesHash == derived.UnclesHash, header, "proof-of-work fields");
    }

    /// <summary>The timestamp derivation gives the child of <paramref name="parent"/>.</summary>
    public static ulong Timestamp(BlockHeader parent, EezSettlementContext context) =>
        parent.Timestamp > ulong.MaxValue - context.BlockTimeSeconds ? ulong.MaxValue : parent.Timestamp + context.BlockTimeSeconds;

    private static void Require(bool condition, BlockHeader header, string field)
    {
        if (!condition)
        {
            throw new EezSettlementException($"Block {header.Number} has {field}, so derivation cannot rebuild it.");
        }
    }
}
