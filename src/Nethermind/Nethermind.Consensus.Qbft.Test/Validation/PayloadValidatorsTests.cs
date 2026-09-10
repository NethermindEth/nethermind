// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Consensus.Qbft.Validation;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Validation;

/// <summary>Port of Besu's <c>PrepareValidatorTest</c>, <c>CommitValidatorTest</c>, <c>RoundChangePayloadValidatorTest</c> and <c>ProposalPayloadValidatorTest</c>.</summary>
[Parallelizable(ParallelScope.Self)]
public class PayloadValidatorsTests
{
    private const long ChainHeight = 5;
    private static readonly ConsensusRoundIdentifier TargetRound = new(ChainHeight, 3);
    private readonly List<PrivateKey> _keys = QbftTestData.Keys(4);
    private readonly PrivateKey _nonValidator = new PrivateKeyGenerator().Generate();
    private Address[] _validators = null!;
    private Block _block = null!;
    private Hash256 _digest = null!;
    private Hash256 _commitDigest = null!;

    [OneTimeTearDown]
    public void TearDown() => _nonValidator.Dispose();

    [SetUp]
    public void Setup()
    {
        _validators = QbftTestMessages.Addresses(_keys);
        _block = QbftTestMessages.Block((ulong)ChainHeight, _keys[0].Address, _validators);
        _digest = QbftTestMessages.Digest(_block);
        _commitDigest = new Hash256(QbftTestMessages.BlockInterface.Digest(QbftTestMessages.BlockInterface.ReplaceRound(_block, TargetRound.Round)));
    }

    private PrepareValidator PrepareValidator() => new(_validators, TargetRound, _digest, LimboLogs.Instance);
    private CommitValidator CommitValidator() => new(_validators, TargetRound, _digest, _commitDigest, LimboLogs.Instance);
    private RoundChangePayloadValidator RoundChangeValidator() => new(_validators, ChainHeight, LimboLogs.Instance);

    [Test]
    public void PrepareFromValidatorForTargetRoundAndDigestIsValid() =>
        Assert.That(PrepareValidator().Validate(QbftTestMessages.Factory(_keys[1]).CreatePrepare(TargetRound, _digest)), Is.True);

    [Test]
    public void PrepareFromNonValidatorFails() =>
        Assert.That(PrepareValidator().Validate(QbftTestMessages.Factory(_nonValidator).CreatePrepare(TargetRound, _digest)), Is.False);

    [Test]
    public void PrepareForWrongRoundFails() =>
        Assert.That(PrepareValidator().Validate(QbftTestMessages.Factory(_keys[1]).CreatePrepare(new ConsensusRoundIdentifier(ChainHeight, 4), _digest)), Is.False);

    [Test]
    public void PrepareForWrongHeightFails() =>
        Assert.That(PrepareValidator().Validate(QbftTestMessages.Factory(_keys[1]).CreatePrepare(new ConsensusRoundIdentifier(ChainHeight + 1, TargetRound.Round), _digest)), Is.False);

    [Test]
    public void PrepareWithWrongDigestFails() =>
        Assert.That(PrepareValidator().Validate(QbftTestMessages.Factory(_keys[1]).CreatePrepare(TargetRound, Keccak.Compute("other"))), Is.False);

    [Test]
    public void CommitWithMatchingSealIsValid()
    {
        MessageFactory factory = QbftTestMessages.Factory(_keys[1]);
        Commit commit = factory.CreateCommit(TargetRound, _digest, factory.CreateCommitSeal(_block, TargetRound.Round));
        Assert.That(CommitValidator().Validate(commit), Is.True);
    }

    [Test]
    public void CommitSealSignedByAnotherValidatorFails()
    {
        MessageFactory author = QbftTestMessages.Factory(_keys[1]);
        MessageFactory other = QbftTestMessages.Factory(_keys[2]);
        Commit commit = author.CreateCommit(TargetRound, _digest, other.CreateCommitSeal(_block, TargetRound.Round));
        Assert.That(CommitValidator().Validate(commit), Is.False);
    }

    [Test]
    public void CommitSealForWrongRoundFails()
    {
        MessageFactory factory = QbftTestMessages.Factory(_keys[1]);
        Commit commit = factory.CreateCommit(TargetRound, _digest, factory.CreateCommitSeal(_block, TargetRound.Round + 1));
        Assert.That(CommitValidator().Validate(commit), Is.False);
    }

    [Test]
    public void CommitFromNonValidatorFails()
    {
        MessageFactory factory = QbftTestMessages.Factory(_nonValidator);
        Commit commit = factory.CreateCommit(TargetRound, _digest, factory.CreateCommitSeal(_block, TargetRound.Round));
        Assert.That(CommitValidator().Validate(commit), Is.False);
    }

    [Test]
    public void CommitWithWrongDigestFails()
    {
        MessageFactory factory = QbftTestMessages.Factory(_keys[1]);
        Commit commit = factory.CreateCommit(TargetRound, Keccak.Compute("other"), factory.CreateCommitSeal(_block, TargetRound.Round));
        Assert.That(CommitValidator().Validate(commit), Is.False);
    }

    [Test]
    public void RoundChangeWithoutPreparedRoundIsValid() =>
        Assert.That(RoundChangeValidator().Validate(QbftTestMessages.Factory(_keys[1]).CreateRoundChange(TargetRound, null).SignedPayload), Is.True);

    [Test]
    public void RoundChangeFromNonValidatorFails() =>
        Assert.That(RoundChangeValidator().Validate(QbftTestMessages.Factory(_nonValidator).CreateRoundChange(TargetRound, null).SignedPayload), Is.False);

    [Test]
    public void RoundChangeForWrongHeightFails() =>
        Assert.That(RoundChangeValidator().Validate(QbftTestMessages.Factory(_keys[1]).CreateRoundChange(new ConsensusRoundIdentifier(ChainHeight + 1, 3), null).SignedPayload), Is.False);

    [TestCase(0)]
    [TestCase(RoundChangePayloadValidator.MaxAllowedRound + 1)]
    public void RoundChangeTargetRoundOutOfRangeFails(int round) =>
        Assert.That(RoundChangeValidator().Validate(QbftTestMessages.Factory(_keys[1]).CreateRoundChange(new ConsensusRoundIdentifier(ChainHeight, round), null).SignedPayload), Is.False);

    [TestCase(2, true)]
    [TestCase(3, false)]
    [TestCase(4, false)]
    public void RoundChangePreparedRoundMustBeBeforeTargetRound(int preparedRound, bool expected)
    {
        PreparedCertificate certificate = new(_block, [], preparedRound);
        Assert.That(RoundChangeValidator().Validate(QbftTestMessages.Factory(_keys[1]).CreateRoundChange(TargetRound, certificate).SignedPayload), Is.EqualTo(expected));
    }

    [Test]
    public void ProposalPayloadFromExpectedProposerForRoundIsValid()
    {
        IQbftBlockValidator blockValidator = Substitute.For<IQbftBlockValidator>();
        blockValidator.ValidateBlock(Arg.Any<Block>(), Arg.Any<ReadOnlyBlockAccessList?>()).Returns(BlockValidationResult.Valid);
        ProposalPayloadValidator validator = new(_keys[0].Address, TargetRound, blockValidator, LimboLogs.Instance);
        Proposal proposal = QbftTestMessages.Factory(_keys[0]).CreateProposal(TargetRound, _block, null, [], []);
        Assert.That(validator.Validate(proposal.SignedPayload), Is.True);
    }

    [Test]
    public void ProposalPayloadFromWrongProposerFails()
    {
        ProposalPayloadValidator validator = new(_keys[0].Address, TargetRound, null, LimboLogs.Instance);
        Proposal proposal = QbftTestMessages.Factory(_keys[1]).CreateProposal(TargetRound, _block, null, [], []);
        Assert.That(validator.ValidateWithoutBlockValidation(proposal.SignedPayload), Is.False);
    }

    [Test]
    public void ProposalPayloadForWrongRoundFails()
    {
        ProposalPayloadValidator validator = new(_keys[0].Address, TargetRound, null, LimboLogs.Instance);
        Proposal proposal = QbftTestMessages.Factory(_keys[0]).CreateProposal(new ConsensusRoundIdentifier(ChainHeight, 4), _block, null, [], []);
        Assert.That(validator.ValidateWithoutBlockValidation(proposal.SignedPayload), Is.False);
    }

    [Test]
    public void ProposalPayloadWithBlockNumberNotMatchingSequenceFails()
    {
        ProposalPayloadValidator validator = new(_keys[0].Address, TargetRound, null, LimboLogs.Instance);
        Block wrongHeight = QbftTestMessages.Block((ulong)ChainHeight + 1, _keys[0].Address, _validators);
        Proposal proposal = QbftTestMessages.Factory(_keys[0]).CreateProposal(TargetRound, wrongHeight, null, [], []);
        Assert.That(validator.ValidateWithoutBlockValidation(proposal.SignedPayload), Is.False);
    }

    [Test]
    public void ProposalPayloadFailingBlockValidationFails()
    {
        IQbftBlockValidator blockValidator = Substitute.For<IQbftBlockValidator>();
        blockValidator.ValidateBlock(Arg.Any<Block>(), Arg.Any<ReadOnlyBlockAccessList?>()).Returns(BlockValidationResult.Invalid("bad"));
        ProposalPayloadValidator validator = new(_keys[0].Address, TargetRound, blockValidator, LimboLogs.Instance);
        Proposal proposal = QbftTestMessages.Factory(_keys[0]).CreateProposal(TargetRound, _block, null, [], []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(validator.Validate(proposal.SignedPayload), Is.False);
            Assert.That(validator.ValidateWithoutBlockValidation(proposal.SignedPayload), Is.True);
        }
    }

    [Test]
    public void HelpersDetectDuplicateAuthorsAndRoundMismatches()
    {
        SignedData<PreparePayload> a = QbftTestMessages.Factory(_keys[0]).CreatePrepare(TargetRound, _digest).SignedPayload;
        SignedData<PreparePayload> b = QbftTestMessages.Factory(_keys[1]).CreatePrepare(TargetRound, _digest).SignedPayload;
        SignedData<PreparePayload> otherRound = QbftTestMessages.Factory(_keys[2]).CreatePrepare(new ConsensusRoundIdentifier(ChainHeight, 2), _digest).SignedPayload;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ValidationHelpers.HasDuplicateAuthors<PreparePayload>([a, b]), Is.False);
            Assert.That(ValidationHelpers.HasDuplicateAuthors<PreparePayload>([a, a]), Is.True);
            Assert.That(ValidationHelpers.HasSufficientEntries<PreparePayload>([a, b], 2), Is.True);
            Assert.That(ValidationHelpers.HasSufficientEntries<PreparePayload>([a], 2), Is.False);
            Assert.That(ValidationHelpers.AllMessagesTargetRound<PreparePayload>([a, b], TargetRound), Is.True);
            Assert.That(ValidationHelpers.AllMessagesTargetRound<PreparePayload>([a, otherRound], TargetRound), Is.False);
        }
    }
}
