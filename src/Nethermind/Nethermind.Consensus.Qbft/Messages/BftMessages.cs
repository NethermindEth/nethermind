// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft.Messages;

/// <summary>A decoded QBFT wire message: a signed payload plus, for some types, unsigned piggy-backed data.</summary>
public abstract class BftMessage
{
    /// <summary>Upper bound on piggy-backed round changes / prepares accepted from the wire.</summary>
    public const int MaxListEntries = 512;

    public abstract Address Author { get; }
    public abstract ConsensusRoundIdentifier RoundIdentifier { get; }
    public abstract int MessageType { get; }
}

public abstract class BftMessage<T>(SignedData<T> signedPayload) : BftMessage where T : QbftPayload
{
    public SignedData<T> SignedPayload { get; } = signedPayload;
    public T Payload => SignedPayload.Payload;
    public override Address Author => SignedPayload.Author;
    public override ConsensusRoundIdentifier RoundIdentifier => Payload.RoundIdentifier;
    public override int MessageType => Payload.MessageType;

    public override string ToString() => $"{GetType().Name}[{SignedPayload}]";
}

public sealed class Prepare(SignedData<PreparePayload> signedPayload) : BftMessage<PreparePayload>(signedPayload)
{
    public Hash256 Digest => Payload.Digest;
}

public sealed class Commit(SignedData<CommitPayload> signedPayload) : BftMessage<CommitPayload>(signedPayload)
{
    public Hash256 Digest => Payload.Digest;
    public Signature CommitSeal => Payload.CommitSeal;
}

public sealed class Proposal(
    SignedData<ProposalPayload> signedPayload,
    IReadOnlyList<SignedData<RoundChangePayload>> roundChanges,
    IReadOnlyList<SignedData<PreparePayload>> prepares) : BftMessage<ProposalPayload>(signedPayload)
{
    public IReadOnlyList<SignedData<RoundChangePayload>> RoundChanges { get; } = roundChanges;
    public IReadOnlyList<SignedData<PreparePayload>> Prepares { get; } = prepares;
    public Block Block => Payload.ProposedBlock;
    public ReadOnlyBlockAccessList? BlockAccessList => Payload.BlockAccessList;
}

public sealed class RoundChange(
    SignedData<RoundChangePayload> signedPayload,
    Block? proposedBlock,
    ReadOnlyBlockAccessList? blockAccessList,
    IReadOnlyList<SignedData<PreparePayload>> prepares,
    bool useLegacyEncoding = false) : BftMessage<RoundChangePayload>(signedPayload)
{
    public Block? ProposedBlock { get; } = proposedBlock;
    public ReadOnlyBlockAccessList? BlockAccessList { get; } = blockAccessList;
    public IReadOnlyList<SignedData<PreparePayload>> Prepares { get; } = prepares;

    /// <summary>Encode as the pre-Besu-26.1.0 three-item list without the block access list slot.</summary>
    public bool UseLegacyEncoding { get; } = useLegacyEncoding;

    public PreparedRoundMetadata? PreparedRoundMetadata => Payload.PreparedRoundMetadata;
    public int? PreparedRound => Payload.PreparedRoundMetadata?.PreparedRound;
}
