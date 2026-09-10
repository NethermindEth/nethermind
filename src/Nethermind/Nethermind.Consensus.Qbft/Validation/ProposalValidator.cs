// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.Validation;

/// <summary>
/// Validates a Proposal: payload, and for rounds above zero the piggy-backed round-change quorum and
/// the prepares that justify re-proposing a previously prepared block.
/// </summary>
/// <remarks>Mirrors Besu's <c>ProposalValidator</c>.</remarks>
public sealed class ProposalValidator(
    QbftBlockInterface blockInterface,
    IQbftBlockValidator blockValidator,
    int quorumMessageCount,
    IReadOnlyList<Address> validators,
    ConsensusRoundIdentifier roundIdentifier,
    IProposerSelector proposerSelector,
    ILogManager logManager)
{
    private const string ErrorPrefix = "Invalid Proposal Payload";
    private readonly ILogger _logger = logManager.GetClassLogger<ProposalValidator>();

    public bool Validate(Proposal message) => Validate(message, true);

    public bool ValidateWithoutBlockValidation(Proposal message) => Validate(message, false);

    private bool Validate(Proposal message, bool validateBlock)
    {
        Address expectedProposer = proposerSelector.SelectProposerForRound(roundIdentifier);
        ProposalPayloadValidator payloadValidator = new(expectedProposer, roundIdentifier, blockValidator, logManager);
        bool payloadValid = validateBlock
            ? payloadValidator.Validate(message.SignedPayload)
            : payloadValidator.ValidateWithoutBlockValidation(message.SignedPayload);
        if (!payloadValid)
        {
            Log("invalid proposal payload in proposal message");
            return false;
        }

        return ValidateProposalAndRoundChangeAreConsistent(message);
    }

    private bool ValidateProposalAndRoundChangeAreConsistent(Proposal proposal)
    {
        if (proposal.RoundIdentifier.Round == 0)
        {
            if (proposal.RoundChanges.Count != 0 || proposal.Prepares.Count != 0)
            {
                Log("round-0 proposal must not contain any prepares or roundchanges");
                return false;
            }

            return ValidateBlockCoinbaseMatchesMsgAuthor(proposal);
        }

        if (!ValidateRoundChanges(proposal, proposal.RoundChanges))
        {
            Log("failed to validate piggy-backed round change payloads");
            return false;
        }

        // The payload validator already ensures prepared round < target round.
        SignedData<RoundChangePayload>? latestPrepared = GetRoundChangeWithLatestPreparedRound(proposal.RoundChanges);
        if (latestPrepared is null)
        {
            if (proposal.Prepares.Count != 0)
            {
                Log("No PreparedMetadata exists, so prepare list must be empty");
                return false;
            }

            return ValidateBlockCoinbaseMatchesMsgAuthor(proposal);
        }

        PreparedRoundMetadata metadata = latestPrepared.Payload.PreparedRoundMetadata!;
        // The prepared digest was computed over the block carrying the OLD round, so re-derive it from the proposed block.
        Block blockWithOldRound = blockInterface.ReplaceRound(proposal.Block, metadata.PreparedRound);
        Hash256 expectedPriorDigest = new(blockInterface.Digest(blockWithOldRound));
        if (metadata.PreparedBlockHash != expectedPriorDigest)
        {
            Log($"Latest Prepared Metadata blockhash does not align with proposed block. Expected: {expectedPriorDigest}, Actual: {metadata.PreparedBlockHash}");
            return false;
        }

        if (!ValidatePrepares(metadata, proposal.RoundIdentifier.Sequence, proposal.Prepares))
        {
            Log("Piggy-backed prepares failed validation");
            return false;
        }

        return true;
    }

    private bool ValidateBlockCoinbaseMatchesMsgAuthor(Proposal message)
    {
        if (QbftBlockInterface.GetProposer(message.Block.Header) != message.Author)
        {
            Log("block coinbase does not match the proposer's address");
            return false;
        }

        return true;
    }

    private bool ValidateRoundChanges(Proposal proposal, IReadOnlyList<SignedData<RoundChangePayload>> roundChanges)
    {
        if (ValidationHelpers.HasDuplicateAuthors(roundChanges))
        {
            Log("multiple round changes from the same author.");
            return false;
        }

        if (!ValidationHelpers.HasSufficientEntries(roundChanges, quorumMessageCount))
        {
            Log("Insufficient round changes for proposal");
            return false;
        }

        if (!MetadataIsConsistentAcrossRoundChanges(roundChanges))
        {
            return false;
        }

        RoundChangePayloadValidator payloadValidator = new(validators, roundIdentifier.Sequence, logManager);
        for (int i = 0; i < roundChanges.Count; i++)
        {
            if (!payloadValidator.Validate(roundChanges[i]))
            {
                Log("invalid proposal, round changes did not pass validation");
                return false;
            }
        }

        // The payload validator only checks the height, not the round.
        if (!ValidationHelpers.AllMessagesTargetRound(roundChanges, proposal.RoundIdentifier))
        {
            Log("not all roundChange payloads target the proposal round.");
            return false;
        }

        return true;
    }

    private bool ValidatePrepares(PreparedRoundMetadata metadata, long currentHeight, IReadOnlyList<SignedData<PreparePayload>> prepares)
    {
        if (ValidationHelpers.HasDuplicateAuthors(prepares))
        {
            Log("multiple prepares from the same author.");
            return false;
        }

        if (!ValidationHelpers.HasSufficientEntries(prepares, quorumMessageCount))
        {
            Log("Insufficient prepares for proposal");
            return false;
        }

        ConsensusRoundIdentifier preparedRound = new(currentHeight, metadata.PreparedRound);
        PrepareValidator prepareValidator = new(validators, preparedRound, metadata.PreparedBlockHash, logManager);
        for (int i = 0; i < prepares.Count; i++)
        {
            if (!prepareValidator.Validate(prepares[i]))
            {
                Log("Prepare failed validation");
                return false;
            }
        }

        return true;
    }

    private bool MetadataIsConsistentAcrossRoundChanges(IReadOnlyList<SignedData<RoundChangePayload>> roundChanges)
    {
        Dictionary<int, Hash256> digestByRound = [];
        for (int i = 0; i < roundChanges.Count; i++)
        {
            PreparedRoundMetadata? metadata = roundChanges[i].Payload.PreparedRoundMetadata;
            if (metadata is null) continue;
            if (digestByRound.TryGetValue(metadata.PreparedRound, out Hash256? digest) && digest != metadata.PreparedBlockHash)
            {
                Log("Roundchanges have different prepared metadata for same round");
                return false;
            }

            digestByRound[metadata.PreparedRound] = metadata.PreparedBlockHash;
        }

        return true;
    }

    /// <summary>The round change carrying the highest prepared round, or null when none is prepared.</summary>
    public static SignedData<RoundChangePayload>? GetRoundChangeWithLatestPreparedRound(IReadOnlyList<SignedData<RoundChangePayload>> roundChanges)
    {
        SignedData<RoundChangePayload>? best = null;
        for (int i = 0; i < roundChanges.Count; i++)
        {
            PreparedRoundMetadata? metadata = roundChanges[i].Payload.PreparedRoundMetadata;
            if (metadata is null) continue;
            if (best is null || metadata.PreparedRound > best.Payload.PreparedRoundMetadata!.PreparedRound)
            {
                best = roundChanges[i];
            }
        }

        return best;
    }

    private void Log(string message)
    {
        if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: {message}");
    }
}
