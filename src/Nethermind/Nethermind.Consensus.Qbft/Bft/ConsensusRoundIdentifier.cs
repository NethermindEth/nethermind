// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>
/// Identifies one consensus round: the block height being agreed (<see cref="Sequence"/>) and the
/// zero-based attempt at that height (<see cref="Round"/>).
/// </summary>
public readonly record struct ConsensusRoundIdentifier(long Sequence, int Round) : IComparable<ConsensusRoundIdentifier>
{
    public int CompareTo(ConsensusRoundIdentifier other)
    {
        int sequenceComparison = ((ulong)Sequence).CompareTo((ulong)other.Sequence);
        return sequenceComparison != 0 ? sequenceComparison : Round.CompareTo(other.Round);
    }

    public override string ToString() => $"{{Sequence={Sequence}, Round={Round}}}";

    public static bool operator <(ConsensusRoundIdentifier left, ConsensusRoundIdentifier right) => left.CompareTo(right) < 0;
    public static bool operator >(ConsensusRoundIdentifier left, ConsensusRoundIdentifier right) => left.CompareTo(right) > 0;
    public static bool operator <=(ConsensusRoundIdentifier left, ConsensusRoundIdentifier right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ConsensusRoundIdentifier left, ConsensusRoundIdentifier right) => left.CompareTo(right) >= 0;
}
