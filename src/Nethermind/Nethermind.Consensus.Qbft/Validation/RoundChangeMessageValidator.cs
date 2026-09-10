// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.Validation;

/// <summary>Validates a RoundChange: its payload and, when it carries a prepared block, that block and its prepare quorum.</summary>
public sealed class RoundChangeMessageValidator(
    RoundChangePayloadValidator payloadValidator,
    long quorumMessageCount,
    long chainHeight,
    IReadOnlyList<Address> validators,
    QbftBlockInterface blockInterface,
    IQbftBlockValidator blockValidator,
    ILogManager logManager)
{
    private const string ErrorPrefix = "Invalid RoundChange Message";
    private readonly ILogger _logger = logManager.GetClassLogger<RoundChangeMessageValidator>();

    public bool Validate(RoundChange message)
    {
        if (!payloadValidator.Validate(message.SignedPayload))
        {
            Log("embedded payload was invalid");
            return false;
        }

        return message.ProposedBlock is not null ? ValidateWithBlock(message, message.ProposedBlock) : message.PreparedRoundMetadata is null;
    }

    private bool ValidateWithBlock(RoundChange message, Block block)
    {
        if (!ValidateBlock(block, message.BlockAccessList))
        {
            return false;
        }

        if (message.PreparedRoundMetadata is null)
        {
            Log("Prepared block specified, but prepared metadata absent");
            return false;
        }

        PreparedRoundMetadata metadata = message.PreparedRoundMetadata;
        Hash256 blockDigest = new(blockInterface.Digest(block));
        if (metadata.PreparedBlockHash != blockDigest)
        {
            Log("Prepared metadata hash does not match supplied block");
            return false;
        }

        return ValidatePrepares(metadata, message.Prepares);
    }

    private bool ValidateBlock(Block block, ReadOnlyBlockAccessList? blockAccessList)
    {
        BlockValidationResult result = blockValidator.ValidateBlock(block, blockAccessList);
        if (!result.Success)
        {
            Log($"block did not pass validation. Reason {result.ErrorMessage}");
            return false;
        }

        return true;
    }

    private bool ValidatePrepares(PreparedRoundMetadata metadata, IReadOnlyList<SignedData<PreparePayload>> prepares)
    {
        ConsensusRoundIdentifier preparedRound = new(chainHeight, metadata.PreparedRound);
        PrepareValidator prepareValidator = new(validators, preparedRound, metadata.PreparedBlockHash, logManager);
        if (ValidationHelpers.HasDuplicateAuthors(prepares))
        {
            Log("multiple prepares from the same author.");
            return false;
        }

        if (!ValidationHelpers.HasSufficientEntries(prepares, quorumMessageCount))
        {
            Log("insufficient Prepare messages piggybacked.");
            return false;
        }

        for (int i = 0; i < prepares.Count; i++)
        {
            if (!prepareValidator.Validate(prepares[i])) return false;
        }

        return true;
    }

    private void Log(string message)
    {
        if (_logger.IsInfo) _logger.Info($"{ErrorPrefix}: {message}");
    }
}
