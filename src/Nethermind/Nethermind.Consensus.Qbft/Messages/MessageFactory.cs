// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft.Messages;

/// <summary>Builds and signs the local node's QBFT messages.</summary>
/// <param name="useLegacyEncoding">Emit the pre-Besu-26.1.0 shapes without the block access list slot.</param>
public sealed class MessageFactory(ISigner signer, QbftMessageCodec codec, BftBlockInterface blockInterface, bool useLegacyEncoding = false)
{
    public Address LocalAddress => signer.Address;

    public Proposal CreateProposal(
        ConsensusRoundIdentifier roundIdentifier,
        Block block,
        ReadOnlyBlockAccessList? blockAccessList,
        IReadOnlyList<SignedData<RoundChangePayload>> roundChanges,
        IReadOnlyList<SignedData<PreparePayload>> prepares)
    {
        ProposalPayload payload = new(roundIdentifier, block, blockAccessList, useLegacyEncoding);
        return new Proposal(Sign(payload), roundChanges, prepares);
    }

    public Prepare CreatePrepare(ConsensusRoundIdentifier roundIdentifier, Hash256 digest) => new(Sign(new PreparePayload(roundIdentifier, digest)));

    public Commit CreateCommit(ConsensusRoundIdentifier roundIdentifier, Hash256 digest, Signature commitSeal) =>
        new(Sign(new CommitPayload(roundIdentifier, digest, commitSeal)));

    public RoundChange CreateRoundChange(ConsensusRoundIdentifier roundIdentifier, PreparedCertificate? preparedCertificate)
    {
        if (preparedCertificate is null)
        {
            return new RoundChange(Sign(new RoundChangePayload(roundIdentifier, null)), null, null, [], useLegacyEncoding);
        }

        Hash256 preparedDigest = new(blockInterface.Digest(preparedCertificate.Block));
        RoundChangePayload payload = new(roundIdentifier, new PreparedRoundMetadata(preparedDigest, preparedCertificate.Round));
        return new RoundChange(Sign(payload), preparedCertificate.Block, preparedCertificate.BlockAccessList, preparedCertificate.Prepares, useLegacyEncoding);
    }

    /// <summary>Signs the digest a validator commits to: the proposal header with the given round and no seals.</summary>
    public Signature CreateCommitSeal(Block block, int round)
    {
        ValueHash256 digest = blockInterface.Digest(blockInterface.ReplaceRound(block, round));
        return SignOrThrow(in digest);
    }

    private SignedData<T> Sign<T>(T payload) where T : QbftPayload
    {
        ValueHash256 hash = codec.HashForSignature(payload);
        return new SignedData<T>(payload, SignOrThrow(in hash), signer.Address);
    }

    private Signature SignOrThrow(in ValueHash256 hash) =>
        signer.TrySign(in hash, out Signature signature)
            ? signature
            : throw new InvalidOperationException($"Signer {signer.Address} cannot sign QBFT messages: no key is configured.");
}
