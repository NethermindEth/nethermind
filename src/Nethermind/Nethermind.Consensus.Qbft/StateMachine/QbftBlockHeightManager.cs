// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.Validation;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>Drives consensus for one block height across its rounds.</summary>
public interface IBlockHeightManager
{
    long ChainHeight { get; }
    BlockHeader ParentBlockHeader { get; }
    QbftRound? CurrentRound { get; }
    RoundChangeManager? RoundChangeManager { get; }
    void HandleBlockTimerExpiry(ConsensusRoundIdentifier roundIdentifier);
    void RoundExpired(RoundExpiryEvent expiry);
    void HandleProposalPayload(Proposal proposal);
    void HandlePreparePayload(Prepare prepare);
    void HandleCommitPayload(Commit commit);
    void HandleRoundChangePayload(RoundChange roundChange);
}

/// <summary>Height manager of a node that is not a validator: it observes only.</summary>
public sealed class NoOpBlockHeightManager(BlockHeader parentHeader) : IBlockHeightManager
{
    public long ChainHeight => (long)parentHeader.Number + 1;
    public BlockHeader ParentBlockHeader => parentHeader;
    public QbftRound? CurrentRound => null;
    public RoundChangeManager? RoundChangeManager => null;
    public void HandleBlockTimerExpiry(ConsensusRoundIdentifier roundIdentifier) { }
    public void RoundExpired(RoundExpiryEvent expiry) { }
    public void HandleProposalPayload(Proposal proposal) { }
    public void HandlePreparePayload(Prepare prepare) { }
    public void HandleCommitPayload(Commit commit) { }
    public void HandleRoundChangePayload(RoundChange roundChange) { }
}

/// <summary>
/// Validator's height manager: starts rounds, buffers messages for future rounds, performs round
/// changes and hands the proposer the artifacts to start a new round.
/// </summary>
/// <remarks>Mirrors Besu's <c>QbftBlockHeightManager</c>, including the empty-block period and early round change.</remarks>
public sealed class QbftBlockHeightManager : IBlockHeightManager
{
    private enum MessageAge
    {
        PriorRound,
        CurrentRound,
        FutureRound
    }

    private readonly QbftRoundFactory _roundFactory;
    private readonly IValidatorProvider _validatorProvider;
    private readonly RoundChangeManager _roundChangeManager;
    private readonly QbftMessageTransmitter _transmitter;
    private readonly MessageFactory _messageFactory;
    private readonly Dictionary<int, RoundState> _futureRoundStateBuffer = [];
    private readonly FutureRoundProposalMessageValidator _futureRoundProposalMessageValidator;
    private readonly MessageValidatorFactory _messageValidatorFactory;
    private readonly IQbftFinalState _finalState;
    private readonly QbftBlockInterface _blockInterface;
    private readonly bool _isEarlyRoundChangeEnabled;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private PreparedCertificate? _latestPreparedCertificate;
    private QbftRound? _currentRound;

    public QbftBlockHeightManager(
        BlockHeader parentHeader,
        IQbftFinalState finalState,
        RoundChangeManager roundChangeManager,
        QbftRoundFactory roundFactory,
        MessageValidatorFactory messageValidatorFactory,
        MessageFactory messageFactory,
        QbftMessageTransmitter transmitter,
        IValidatorProvider validatorProvider,
        QbftBlockInterface blockInterface,
        ILogManager logManager,
        bool isEarlyRoundChangeEnabled = false)
    {
        ParentBlockHeader = parentHeader;
        _roundFactory = roundFactory;
        _validatorProvider = validatorProvider;
        _transmitter = transmitter;
        _messageFactory = messageFactory;
        _roundChangeManager = roundChangeManager;
        _finalState = finalState;
        _blockInterface = blockInterface;
        _messageValidatorFactory = messageValidatorFactory;
        _isEarlyRoundChangeEnabled = isEarlyRoundChangeEnabled;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<QbftBlockHeightManager>();
        _futureRoundProposalMessageValidator = messageValidatorFactory.CreateFutureRoundProposalMessageValidator(ChainHeight, parentHeader);

        finalState.BlockTimer.StartTimer(new ConsensusRoundIdentifier(ChainHeight, 0), parentHeader.Timestamp);
    }

    public long ChainHeight => (long)ParentBlockHeader.Number + 1;
    public BlockHeader ParentBlockHeader { get; }
    public QbftRound? CurrentRound => _currentRound;
    public RoundChangeManager? RoundChangeManager => _roundChangeManager;

    public void HandleBlockTimerExpiry(ConsensusRoundIdentifier roundIdentifier)
    {
        // The proposal can beat the block timer (timer precision), in which case the round already exists.
        if (_currentRound is not null)
        {
            return;
        }

        StartNewRound(0);
        QbftRound round = _currentRound!;
        LogValidatorChanges(round);
        if (roundIdentifier == round.RoundIdentifier)
        {
            BuildBlockAndMaybePropose(roundIdentifier, round);
        }
        else if (_logger.IsTrace)
        {
            _logger.Trace($"Block timer expired for a round ({roundIdentifier}) other than current ({round.RoundIdentifier})");
        }
    }

    private void BuildBlockAndMaybePropose(ConsensusRoundIdentifier roundIdentifier, QbftRound round)
    {
        long nowMillis = _finalState.Clock.UnixTime.MillisecondsLong;
        if (!_finalState.IsLocalNodeProposerForRound(round.RoundIdentifier))
        {
            bool withinEmptyBlockPeriod = !_finalState.BlockTimer.CheckEmptyBlockExpired(ParentBlockHeader.Timestamp, nowMillis);
            // Stay idle only while the chain is genuinely quiet: if a transaction arrived during the empty block
            // period the proposer may have crashed, so a non-proposer must keep the round alive to unblock the chain.
            if (withinEmptyBlockPeriod && WouldCreateEmptyBlock(round))
            {
                GoIdleForEmptyBlockPeriod(roundIdentifier, nowMillis);
            }

            if (_logger.IsTrace) _logger.Trace($"This node is not a proposer so it will not send a proposal: {roundIdentifier}");
            return;
        }

        BlockCreationResult created = round.CreateBlock(HeaderTimestampSeconds());
        if (!IsEmptyBlock(created.Block))
        {
            if (_logger.IsTrace) _logger.Trace($"Block is not empty and this node is a proposer so it will send a proposal: {roundIdentifier}");
            round.UpdateStateWithProposalAndTransmit(created.Block, created.BlockAccessList, [], []);
            return;
        }

        if (_finalState.BlockTimer.CheckEmptyBlockExpired(ParentBlockHeader.Timestamp, nowMillis))
        {
            if (_logger.IsTrace) _logger.Trace($"Block has no transactions and this node is a proposer so it will send a proposal: {roundIdentifier}");
            round.UpdateStateWithProposalAndTransmit(created.Block, created.BlockAccessList, [], []);
        }
        else
        {
            if (_logger.IsTrace) _logger.Trace($"Block has no transactions but emptyBlockPeriodSeconds did not expire yet: {roundIdentifier}");
            GoIdleForEmptyBlockPeriod(roundIdentifier, nowMillis);
        }
    }

    private void GoIdleForEmptyBlockPeriod(ConsensusRoundIdentifier roundIdentifier, long nowMillis)
    {
        _finalState.BlockTimer.ResetTimerForEmptyBlock(roundIdentifier, ParentBlockHeader.Timestamp, nowMillis);
        _finalState.RoundTimer.CancelTimer();
        _currentRound = null;
    }

    private bool WouldCreateEmptyBlock(QbftRound round) => IsEmptyBlock(round.CreateBlock(HeaderTimestampSeconds()).Block);

    /// <summary>A block with no transactions and no validator vote adds nothing to the chain.</summary>
    private bool IsEmptyBlock(Block block)
    {
        if (block.Transactions.Length != 0)
        {
            return false;
        }

        try
        {
            return _blockInterface.GetExtraData(block.Header).Vote is null;
        }
        catch (Exception e) when (e is Serialization.Rlp.RlpException or ArgumentException or IndexOutOfRangeException)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to decode extra data for block {block.Number} while checking for empty block: {e.Message}");
            return true;
        }
    }

    private ulong HeaderTimestampSeconds() => (ulong)Math.Round(_finalState.Clock.UnixTime.MillisecondsLong / 1000.0);

    private void LogValidatorChanges(QbftRound round)
    {
        if (round.RoundIdentifier.Round != 0 || !_logger.IsInfo)
        {
            return;
        }

        IReadOnlyList<Address> previous = _validatorProvider.GetValidatorsForBlock(ParentBlockHeader);
        IReadOnlyList<Address> current = _validatorProvider.GetValidatorsAfterBlock(ParentBlockHeader);
        if (!new HashSet<Address>(previous).SetEquals(current))
        {
            _logger.Info($"QBFT Validator list change. Previous chain height {ParentBlockHeader.Number}: [{string.Join(", ", previous)}]. Current chain height {ChainHeight}: [{string.Join(", ", current)}].");
        }
    }

    public void RoundExpired(RoundExpiryEvent expiry)
    {
        if (_currentRound is null)
        {
            if (_logger.IsError) _logger.Error($"Received Round timer expiry before round is created timerRound={expiry.View}");
            return;
        }

        if (expiry.View != _currentRound.RoundIdentifier)
        {
            if (_logger.IsTrace) _logger.Trace($"Ignoring Round timer expired which does not match current round. round={_currentRound.RoundIdentifier}, timerRound={expiry.View}");
            return;
        }

        DoRoundChange(_currentRound.RoundIdentifier.Round + 1);
    }

    private void DoRoundChange(int newRoundNumber)
    {
        if (_currentRound is not null && _currentRound.RoundIdentifier.Round >= newRoundNumber)
        {
            return;
        }

        if (_logger.IsDebug) _logger.Debug($"Round has expired or changing based on RC quorum, creating PreparedCertificate and notifying peers. round={_currentRound?.RoundIdentifier}");
        PreparedCertificate? prepared = _currentRound?.ConstructPreparedCertificate();
        if (prepared is not null)
        {
            _latestPreparedCertificate = prepared;
        }

        StartNewRound(newRoundNumber);
        QbftRound newRound = _currentRound!;
        try
        {
            // The locally created RoundChange may itself complete the quorum, so it goes through the normal path.
            RoundChange localRoundChange = _messageFactory.CreateRoundChange(newRound.RoundIdentifier, _latestPreparedCertificate);
            HandleRoundChangePayload(localRoundChange);
        }
        catch (InvalidOperationException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to create signed RoundChange message. {e.Message}");
        }

        _transmitter.MulticastRoundChange(newRound.RoundIdentifier, _latestPreparedCertificate);
    }

    public void HandleProposalPayload(Proposal proposal)
    {
        if (_logger.IsTrace) _logger.Trace("Received a Proposal Payload.");
        MessageAge age = DetermineAgeOfPayload(proposal.RoundIdentifier.Round);
        if (age == MessageAge.PriorRound)
        {
            if (_logger.IsTrace) _logger.Trace($"Received Proposal Payload for a prior round={proposal.RoundIdentifier}");
            return;
        }

        if (age == MessageAge.FutureRound)
        {
            if (!_futureRoundProposalMessageValidator.ValidateProposalMessage(proposal))
            {
                if (_logger.IsInfo) _logger.Info("Received future Proposal which is illegal, no round change triggered.");
                return;
            }

            StartNewRound(proposal.RoundIdentifier.Round);
        }

        _currentRound?.HandleProposalMessage(proposal);
    }

    public void HandlePreparePayload(Prepare prepare)
    {
        if (_logger.IsTrace) _logger.Trace("Received a Prepare Payload.");
        ActionOrBufferMessage(prepare, static (round, message) => round.HandlePrepareMessage(message), static (state, message) => state.AddPrepareMessage(message));
    }

    public void HandleCommitPayload(Commit commit)
    {
        if (_logger.IsTrace) _logger.Trace("Received a Commit Payload.");
        ActionOrBufferMessage(commit, static (round, message) => round.HandleCommitMessage(message), static (state, message) => state.AddCommitMessage(message));
    }

    private void ActionOrBufferMessage<T>(T message, Action<QbftRound, T> inRoundHandler, Action<RoundState, T> buffer) where T : BftMessage
    {
        MessageAge age = DetermineAgeOfPayload(message.RoundIdentifier.Round);
        if (age == MessageAge.CurrentRound)
        {
            if (_currentRound is not null)
            {
                inRoundHandler(_currentRound, message);
            }
        }
        else if (age == MessageAge.FutureRound)
        {
            ConsensusRoundIdentifier roundIdentifier = message.RoundIdentifier;
            if (!_futureRoundStateBuffer.TryGetValue(roundIdentifier.Round, out RoundState? state))
            {
                state = new RoundState(roundIdentifier, _finalState.Quorum, _messageValidatorFactory.CreateMessageValidator(roundIdentifier, ParentBlockHeader), _logManager);
                _futureRoundStateBuffer[roundIdentifier.Round] = state;
            }

            buffer(state, message);
        }
    }

    public void HandleRoundChangePayload(RoundChange message)
    {
        ConsensusRoundIdentifier targetRound = message.RoundIdentifier;
        if (_logger.IsDebug) _logger.Debug($"Round change from {message.Author}: block {targetRound.Sequence}, round {targetRound.Round}");

        _roundChangeManager.StoreAndLogRoundChangeSummary(message);

        MessageAge age = DetermineAgeOfPayload(targetRound.Round);
        if (age == MessageAge.PriorRound)
        {
            if (_logger.IsDebug) _logger.Debug($"Received RoundChange Payload for a prior round. targetRound={targetRound}");
            return;
        }

        IReadOnlyCollection<RoundChange>? certificate = _roundChangeManager.AppendRoundChangeMessage(message);
        if (!_isEarlyRoundChangeEnabled)
        {
            if (certificate is null)
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"Received sufficient RoundChange messages to change round to targetRound={targetRound}");
            if (age == MessageAge.FutureRound)
            {
                StartNewRound(targetRound.Round);
            }

            RoundChangeArtifacts artifacts = RoundChangeArtifacts.Create(certificate);
            if (_finalState.IsLocalNodeProposerForRound(targetRound))
            {
                if (_currentRound is null)
                {
                    StartNewRound(0);
                }

                _currentRound!.StartRoundWith(artifacts, _finalState.Clock.UnixTime.Seconds);
            }

            return;
        }

        if (_currentRound is null)
        {
            StartNewRound(0);
        }

        int currentRoundNumber = _currentRound!.RoundIdentifier.Round;
        if (targetRound.Round == currentRoundNumber && _finalState.IsLocalNodeProposerForRound(targetRound) && certificate is not null)
        {
            _currentRound.StartRoundWith(RoundChangeArtifacts.Create(certificate), _finalState.Clock.UnixTime.Seconds);
        }

        int? nextHigherRound = _roundChangeManager.FutureRoundChangeQuorumReceived(_currentRound.RoundIdentifier);
        if (nextHigherRound is { } next)
        {
            if (_logger.IsInfo) _logger.Info($"Received sufficient RoundChange messages to change round to targetRound={next}");
            DoRoundChange(next);
        }
    }

    private void StartNewRound(int roundNumber)
    {
        if (_logger.IsDebug) _logger.Debug($"Starting new round {roundNumber}");
        if (_futureRoundStateBuffer.TryGetValue(roundNumber, out RoundState? bufferedState))
        {
            _currentRound = _roundFactory.CreateNewRoundWithState(ParentBlockHeader, bufferedState);
            List<int> stale = [];
            foreach (int round in _futureRoundStateBuffer.Keys)
            {
                if (round <= roundNumber) stale.Add(round);
            }

            foreach (int round in stale) _futureRoundStateBuffer.Remove(round);
        }
        else
        {
            _currentRound = _roundFactory.CreateNewRound(ParentBlockHeader, roundNumber);
        }

        _roundChangeManager.DiscardRoundsPriorTo(_currentRound.RoundIdentifier);
    }

    private MessageAge DetermineAgeOfPayload(int messageRound)
    {
        int currentRound = _currentRound?.RoundIdentifier.Round ?? -1;
        return messageRound > currentRound ? MessageAge.FutureRound
            : messageRound == currentRound ? MessageAge.CurrentRound
            : MessageAge.PriorRound;
    }
}
