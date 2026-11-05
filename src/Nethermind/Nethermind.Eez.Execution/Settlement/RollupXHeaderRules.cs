// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// RollupX derives every header field but the beneficiary and extra data the DA carries: block <c>n</c> sits on the
/// genesis grid, gas is fixed, the beacon root and blob gas are zero, and the prevRandao is the seed L1 settlements
/// put in effect. A live settlement seeds the blocks after its endpoint with its L1 block's prevRandao; any other keeps
/// the seed already in effect for the descendants.
/// </summary>
public sealed class RollupXHeaderRules(ISpecProvider specProvider, EezChainRules rules) : IDerivedHeaderRules
{
    private readonly RollupXSchedule _schedule = rules.Schedule
        ?? throw new ArgumentException("RollupX header rules need a RollupX chain.", nameof(rules));

    public (BlockHeader Header, IReleaseSpec Spec) Build(BlockHeader parent, Address beneficiary, byte[] extraData, Hash256 prevRandao)
    {
        ulong number = parent.Number + 1;
        ulong timestamp = _schedule.Timestamp(number) ?? throw new EezSettlementException($"Block {number} is past the end of the RollupX grid.");
        IReleaseSpec spec = specProvider.GetSpec(number, timestamp);
        BlockHeader header = new(parent.Hash!, Keccak.OfAnEmptySequenceRlp, beneficiary, UInt256.Zero, number, EezChainRules.RollupXBlockGasLimit, timestamp, extraData)
        {
            TotalDifficulty = parent.TotalDifficulty,
            MixHash = prevRandao,
            Nonce = 0,
            ParentBeaconBlockRoot = Keccak.Zero,
            BaseFeePerGas = BaseFeeCalculator.Calculate(parent, spec),
            ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec),
        };
        return (header, spec);
    }

    public Withdrawal[] Withdrawals(IReleaseSpec spec) => [];

    /// <remarks>
    /// The prevRandao is not checked: a block above the last settlement may follow one the follower has not seen yet.
    /// Derivation checks it against the seed it derives.
    /// </remarks>
    public string? Mismatch(Block block, BlockHeader parent)
    {
        BlockHeader header = block.Header;
        return RollupXHeaderValidator.ProfileMismatch(header, _schedule) is { } profile ? profile
            : header.ExtraData.Length > DaPayloadCodec.MaxExtraData ? $"{header.ExtraData.Length} bytes of extra data"
            : block.Withdrawals is not { Length: 0 } ? "withdrawals other than an empty list"
            : header.Number != parent.Number + 1 || header.ParentHash != parent.Hash ? $"a parent other than {parent.ToString(BlockHeader.Format.Short)}"
            : null;
    }

    /// <exception cref="EezSettlementException">The endpoint cannot anchor the chain from <paramref name="settling"/>.</exception>
    public Hash256 Seed(BlockHeader endpoint, L1Anchor? settling, Hash256? previousSeed = null)
    {
        Hash256 current = endpoint.MixHash ?? throw new EezSettlementException($"Block {endpoint.Number} carries no prevRandao.");
        if (rules.UsesNativeContract || settling is not { } l1)
        {
            return current;
        }

        return _schedule.AnchorKind(endpoint.Number, l1.Timestamp) switch
        {
            RollupXAnchorKind.Live => l1.MixHash,
            RollupXAnchorKind.CatchUp => previousSeed ?? current,
            _ => throw new EezSettlementException(
                $"Block {endpoint.Number} cannot be settled by L1 block {l1.Number} at {l1.Timestamp}: it is no Sync position, follows the L1 block or the L1 block is off the reference's slots."),
        };
    }
}
