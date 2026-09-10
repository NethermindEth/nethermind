// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.Validation;

/// <summary>Helpers shared by the message validators.</summary>
public static class ValidationHelpers
{
    public static bool HasDuplicateAuthors<T>(IReadOnlyList<SignedData<T>> messages) where T : QbftPayload
    {
        HashSet<Address> authors = [];
        for (int i = 0; i < messages.Count; i++)
        {
            if (!authors.Add(messages[i].Author)) return true;
        }

        return false;
    }

    public static bool HasSufficientEntries<T>(IReadOnlyList<SignedData<T>> messages, long requiredCount) where T : QbftPayload =>
        messages.Count >= requiredCount;

    public static bool AllMessagesTargetRound<T>(IReadOnlyList<SignedData<T>> payloads, ConsensusRoundIdentifier requiredRound) where T : QbftPayload
    {
        for (int i = 0; i < payloads.Count; i++)
        {
            if (payloads[i].Payload.RoundIdentifier != requiredRound) return false;
        }

        return true;
    }
}

/// <summary>Prepare must come from a validator, for the expected round, with the expected digest.</summary>
public sealed class PrepareValidator(IReadOnlyList<Address> validators, ConsensusRoundIdentifier targetRound, Hash256 expectedDigest, ILogManager logManager)
{
    private const string ErrorPrefix = "Invalid Prepare Message";
    private readonly ILogger _logger = logManager.GetClassLogger<PrepareValidator>();

    public bool Validate(Prepare message) => Validate(message.SignedPayload);

    public bool Validate(SignedData<PreparePayload> signedPayload)
    {
        if (!validators.ContainsAddress(signedPayload.Author))
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: did not originate from a recognized validator.");
            return false;
        }

        PreparePayload payload = signedPayload.Payload;
        if (payload.RoundIdentifier != targetRound)
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: did not target expected round/height");
            return false;
        }

        if (payload.Digest != expectedDigest)
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: did not contain expected digest");
            return false;
        }

        return true;
    }
}

/// <summary>Commit is a valid prepare-shaped message whose seal was made by its author over the commit digest.</summary>
public sealed class CommitValidator(
    IReadOnlyList<Address> validators,
    ConsensusRoundIdentifier targetRound,
    Hash256 expectedDigest,
    Hash256 expectedCommitDigest,
    ILogManager logManager)
{
    private const string ErrorPrefix = "Invalid Commit Message";
    private static readonly EthereumEcdsa _ecdsa = new(0);
    private readonly ILogger _logger = logManager.GetClassLogger<CommitValidator>();

    public bool Validate(Commit message) => Validate(message.SignedPayload);

    public bool Validate(SignedData<CommitPayload> signedPayload)
    {
        if (!validators.ContainsAddress(signedPayload.Author))
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: did not originate from a recognized validator.");
            return false;
        }

        CommitPayload payload = signedPayload.Payload;
        if (payload.RoundIdentifier != targetRound)
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: did not target expected round {targetRound} was {payload.RoundIdentifier}");
            return false;
        }

        if (payload.Digest != expectedDigest)
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: did not contain expected digest {expectedDigest} was {payload.Digest}");
            return false;
        }

        Address? sealCreator = _ecdsa.RecoverAddress(payload.CommitSeal, expectedCommitDigest);
        if (sealCreator != signedPayload.Author)
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: Seal was not created by the message transmitter {sealCreator} was {signedPayload.Author}");
            return false;
        }

        return true;
    }
}

/// <summary>Round change payload: from a validator, for this height, target round in range, prepared round below target.</summary>
public sealed class RoundChangePayloadValidator(IReadOnlyList<Address> validators, long chainHeight, ILogManager logManager)
{
    private const string ErrorPrefix = "Invalid RoundChange Payload";
    public const int MaxAllowedRound = 1000;
    private readonly ILogger _logger = logManager.GetClassLogger<RoundChangePayloadValidator>();

    public bool Validate(SignedData<RoundChangePayload> signedPayload)
    {
        if (!validators.ContainsAddress(signedPayload.Author))
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: did not originate from a recognized validator.");
            return false;
        }

        RoundChangePayload payload = signedPayload.Payload;
        if (payload.RoundIdentifier.Sequence != chainHeight)
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: did not target expected height");
            return false;
        }

        int targetRound = payload.RoundIdentifier.Round;
        if (targetRound <= 0 || targetRound > MaxAllowedRound)
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: target round number out of range [1, {MaxAllowedRound}]");
            return false;
        }

        if (payload.PreparedRoundMetadata is { } metadata)
        {
            if (metadata.PreparedRound >= targetRound)
            {
                if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: prepared metadata is from a round ahead of target round");
                return false;
            }

            if (metadata.PreparedRound < 0)
            {
                if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: prepared metadata is from a negative round number");
                return false;
            }
        }

        return true;
    }
}

/// <summary>Proposal payload: from the expected proposer, for the expected round, block number matching the sequence and (optionally) the block executing cleanly.</summary>
public sealed class ProposalPayloadValidator(Address expectedProposer, ConsensusRoundIdentifier targetRound, IQbftBlockValidator? blockValidator, ILogManager logManager)
{
    private const string ErrorPrefix = "Invalid Proposal Payload";
    private readonly ILogger _logger = logManager.GetClassLogger<ProposalPayloadValidator>();

    public bool Validate(SignedData<ProposalPayload> signedPayload) => Validate(signedPayload, true);

    public bool ValidateWithoutBlockValidation(SignedData<ProposalPayload> signedPayload) => Validate(signedPayload, false);

    private bool Validate(SignedData<ProposalPayload> signedPayload, bool validateBlock)
    {
        if (signedPayload.Author != expectedProposer)
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: proposal created by non-proposer");
            return false;
        }

        ProposalPayload payload = signedPayload.Payload;
        if (payload.RoundIdentifier != targetRound)
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: proposal is not for expected round");
            return false;
        }

        Block block = payload.ProposedBlock;
        if (validateBlock && !ValidateBlock(block, payload.BlockAccessList))
        {
            return false;
        }

        if ((long)block.Number != payload.RoundIdentifier.Sequence)
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: block number does not match sequence number");
            return false;
        }

        return true;
    }

    private bool ValidateBlock(Block block, ReadOnlyBlockAccessList? blockAccessList)
    {
        if (blockValidator is null)
        {
            throw new System.InvalidOperationException("block validation not possible, no block validator.");
        }

        BlockValidationResult result = blockValidator.ValidateBlock(block, blockAccessList);
        if (!result.Success)
        {
            if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: block did not pass validation. Reason {result.ErrorMessage}");
            return false;
        }

        return true;
    }
}
