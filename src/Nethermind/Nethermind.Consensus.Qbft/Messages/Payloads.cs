// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft.Messages;

/// <summary>The signed part of a QBFT message; every payload names the round it targets.</summary>
public abstract record QbftPayload(ConsensusRoundIdentifier RoundIdentifier)
{
    public abstract int MessageType { get; }
}

/// <summary>Prepare: the sender accepts the proposal with the given digest for the round.</summary>
public sealed record PreparePayload(ConsensusRoundIdentifier RoundIdentifier, Hash256 Digest) : QbftPayload(RoundIdentifier)
{
    public override int MessageType => QbftMessageCode.Prepare;
}

/// <summary>Commit: the sender has seen a quorum of prepares and contributes its committed seal.</summary>
public sealed record CommitPayload(ConsensusRoundIdentifier RoundIdentifier, Hash256 Digest, Signature CommitSeal) : QbftPayload(RoundIdentifier)
{
    public override int MessageType => QbftMessageCode.Commit;
}

/// <summary>The round in which a proposal was last prepared and that proposal's digest.</summary>
public sealed record PreparedRoundMetadata(Hash256 PreparedBlockHash, int PreparedRound);

/// <summary>RoundChange: the sender moves to the target round, optionally carrying what it had prepared.</summary>
public sealed record RoundChangePayload(ConsensusRoundIdentifier RoundIdentifier, PreparedRoundMetadata? PreparedRoundMetadata) : QbftPayload(RoundIdentifier)
{
    public override int MessageType => QbftMessageCode.RoundChange;
}

/// <summary>Proposal: the block the proposer puts forward for the round.</summary>
/// <param name="UseLegacyEncoding">
/// Encode as the pre-Besu-26.1.0 three-item list without the block access list slot. A decoded
/// payload remembers the shape it arrived in so that signature verification re-encodes identically.
/// </param>
public sealed record ProposalPayload(
    ConsensusRoundIdentifier RoundIdentifier,
    Block ProposedBlock,
    ReadOnlyBlockAccessList? BlockAccessList,
    bool UseLegacyEncoding = false) : QbftPayload(RoundIdentifier)
{
    public override int MessageType => QbftMessageCode.Proposal;
}
