// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>
/// The decoded consensus payload of a BFT block header's <c>extraData</c> field:
/// 32 bytes of vanity, the validator list, an optional membership vote, the round the block was
/// sealed in and the committed seals.
/// </summary>
public sealed class BftExtraData(
    byte[] vanityData,
    IReadOnlyList<Signature> seals,
    Vote? vote,
    int round,
    IReadOnlyList<Address> validators)
{
    public const int VanityLength = 32;

    public byte[] VanityData { get; } = vanityData;
    public IReadOnlyList<Signature> Seals { get; } = seals;
    public Vote? Vote { get; } = vote;
    public int Round { get; } = round;
    public IReadOnlyList<Address> Validators { get; } = validators;

    /// <summary>Copy with a different round number, keeping every other field.</summary>
    public BftExtraData WithRound(int newRound) => new(VanityData, Seals, Vote, newRound, Validators);

    /// <summary>Copy with the given committed seals and round, keeping vanity, vote and validators.</summary>
    public BftExtraData WithSeals(IReadOnlyList<Signature> newSeals, int newRound) => new(VanityData, newSeals, Vote, newRound, Validators);

    public override string ToString() =>
        $"BftExtraData{{vanity={VanityData.ToHexString()}, validators=[{string.Join(", ", Validators)}], vote={Vote}, round={Round}, seals={Seals.Count}}}";
}
