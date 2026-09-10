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

/// <summary>Validates the prepares and commits that follow an accepted proposal.</summary>
public sealed class SubsequentMessageValidator
{
    private readonly PrepareValidator _prepareValidator;
    private readonly CommitValidator _commitValidator;

    public SubsequentMessageValidator(IReadOnlyList<Address> validators, ConsensusRoundIdentifier targetRound, Block proposalBlock, BftBlockInterface blockInterface, ILogManager logManager)
    {
        Hash256 proposalDigest = new(blockInterface.Digest(proposalBlock));
        Hash256 commitDigest = new(blockInterface.Digest(blockInterface.ReplaceRound(proposalBlock, targetRound.Round)));
        _prepareValidator = new PrepareValidator(validators, targetRound, proposalDigest, logManager);
        _commitValidator = new CommitValidator(validators, targetRound, proposalDigest, commitDigest, logManager);
    }

    public bool Validate(Prepare message) => _prepareValidator.Validate(message);

    public bool Validate(Commit message) => _commitValidator.Validate(message);
}

/// <summary>Per-round validator: accepts one proposal, then validates prepares and commits against it.</summary>
public interface IMessageValidator
{
    bool ValidateProposal(Proposal message);
    bool ValidateProposalWithoutBlockValidation(Proposal message);
    bool ValidatePrepare(Prepare message);
    bool ValidateCommit(Commit message);
}

/// <inheritdoc cref="IMessageValidator"/>
public sealed class MessageValidator(System.Func<Block, SubsequentMessageValidator> subsequentValidatorFactory, ProposalValidator proposalValidator, ILogManager logManager) : IMessageValidator
{
    private readonly ILogger _logger = logManager.GetClassLogger<MessageValidator>();
    private SubsequentMessageValidator? _subsequentValidator;

    public bool ValidateProposal(Proposal message) => ValidateProposal(message, true);

    public bool ValidateProposalWithoutBlockValidation(Proposal message) => ValidateProposal(message, false);

    private bool ValidateProposal(Proposal message, bool validateBlock)
    {
        if (_subsequentValidator is not null)
        {
            if (_logger.IsInfo) _logger.Info("Received subsequent Proposal for current round, discarding.");
            return false;
        }

        bool result = validateBlock ? proposalValidator.Validate(message) : proposalValidator.ValidateWithoutBlockValidation(message);
        if (result)
        {
            _subsequentValidator = subsequentValidatorFactory(message.Block);
        }

        return result;
    }

    public bool ValidatePrepare(Prepare message) => _subsequentValidator?.Validate(message) ?? false;

    public bool ValidateCommit(Commit message) => _subsequentValidator?.Validate(message) ?? false;
}

/// <summary>Validates a proposal for a round ahead of the current one without disturbing the current round's validator.</summary>
public sealed class FutureRoundProposalMessageValidator(MessageValidatorFactory messageValidatorFactory, long chainHeight, BlockHeader parentHeader)
{
    public bool ValidateProposalMessage(Proposal message)
    {
        ConsensusRoundIdentifier round = new(chainHeight, message.RoundIdentifier.Round);
        return messageValidatorFactory.CreateMessageValidator(round, parentHeader).ValidateProposal(message);
    }
}

/// <summary>Builds the validators for a height from the validator set that applies after its parent.</summary>
public sealed class MessageValidatorFactory(
    IProposerSelector proposerSelector,
    IQbftBlockValidator blockValidator,
    IValidatorProvider validatorProvider,
    BftBlockInterface blockInterface,
    ILogManager logManager)
{
    public RoundChangeMessageValidator CreateRoundChangeMessageValidator(long chainHeight, BlockHeader parentHeader)
    {
        IReadOnlyList<Address> validators = validatorProvider.GetValidatorsAfterBlock(parentHeader);
        RoundChangePayloadValidator payloadValidator = new(validators, chainHeight, logManager);
        return new RoundChangeMessageValidator(
            payloadValidator,
            BftHelpers.CalculateRequiredValidatorQuorum(validators.Count),
            chainHeight,
            validators,
            blockInterface,
            blockValidator,
            logManager);
    }

    public MessageValidator CreateMessageValidator(ConsensusRoundIdentifier roundIdentifier, BlockHeader parentHeader)
    {
        IReadOnlyList<Address> validators = validatorProvider.GetValidatorsAfterBlock(parentHeader);
        ProposalValidator proposalValidator = new(
            blockInterface,
            blockValidator,
            BftHelpers.CalculateRequiredValidatorQuorum(validators.Count),
            validators,
            roundIdentifier,
            proposerSelector,
            logManager);
        return new MessageValidator(
            block => new SubsequentMessageValidator(validators, roundIdentifier, block, blockInterface, logManager),
            proposalValidator,
            logManager);
    }

    public FutureRoundProposalMessageValidator CreateFutureRoundProposalMessageValidator(long chainHeight, BlockHeader parentHeader) =>
        new(this, chainHeight, parentHeader);
}
