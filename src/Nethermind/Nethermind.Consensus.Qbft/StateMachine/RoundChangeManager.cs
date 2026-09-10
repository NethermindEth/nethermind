// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.Validation;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>What a proposer needs to start a round after a round-change quorum: the round changes and the best prepared certificate among them.</summary>
public sealed class RoundChangeArtifacts(IReadOnlyList<SignedData<RoundChangePayload>> roundChanges, PreparedCertificate? bestPreparedPeer)
{
    public IReadOnlyList<SignedData<RoundChangePayload>> RoundChanges { get; } = roundChanges;
    public PreparedCertificate? BestPreparedPeer { get; } = bestPreparedPeer;

    public static RoundChangeArtifacts Create(IReadOnlyCollection<RoundChange> roundChanges)
    {
        RoundChange? newest = null;
        List<SignedData<RoundChangePayload>> payloads = new(roundChanges.Count);
        foreach (RoundChange roundChange in roundChanges)
        {
            payloads.Add(roundChange.SignedPayload);
            if (newest is null || ComparePreparedRound(roundChange, newest) > 0)
            {
                newest = roundChange;
            }
        }

        PreparedCertificate? certificate = newest?.ProposedBlock is null
            ? null
            : new PreparedCertificate(newest.ProposedBlock, newest.Prepares, newest.PreparedRound!.Value, newest.BlockAccessList);
        return new RoundChangeArtifacts(payloads, certificate);
    }

    /// <summary>Orders by prepared round, a round change without prepared metadata ranking lowest.</summary>
    private static int ComparePreparedRound(RoundChange left, RoundChange right)
    {
        if (left.PreparedRoundMetadata is null) return -1;
        if (right.PreparedRoundMetadata is null) return 1;
        return left.PreparedRoundMetadata.PreparedRound.CompareTo(right.PreparedRoundMetadata.PreparedRound);
    }
}

/// <summary>
/// Collects round-change messages per target round and reports when a quorum is reached, once per round.
/// </summary>
/// <remarks>
/// With early round change enabled it also tracks the latest round each validator has moved to so
/// that <c>f + 1</c> validators ahead of us trigger a round change without waiting for the timer.
/// </remarks>
/// <param name="futureRoundChangeQuorum">The <c>f + 1</c> threshold for early round change; 0 disables it.</param>
public sealed class RoundChangeManager(long quorum, RoundChangeMessageValidator roundChangeMessageValidator, Address localAddress, ILogManager logManager, long futureRoundChangeQuorum = 0)
{
    private readonly Dictionary<ConsensusRoundIdentifier, RoundChangeStatus> _roundChangeCache = [];
    private readonly Dictionary<Address, ConsensusRoundIdentifier> _roundSummary = [];
    private readonly ILogger _logger = logManager.GetClassLogger<RoundChangeManager>();

    public RoundChangeMessageValidator RoundChangeMessageValidator { get; } = roundChangeMessageValidator;

    /// <summary>Diagnostic: records the round each validator is in and logs a summary once the chain has stalled past round 2.</summary>
    public void StoreAndLogRoundChangeSummary(RoundChange message)
    {
        if (!RoundChangeMessageValidator.Validate(message))
        {
            if (_logger.IsInfo) _logger.Info("RoundChange message is invalid.");
            return;
        }

        _roundSummary[message.Author] = message.RoundIdentifier;

        int lowestTrackedRound = int.MaxValue;
        foreach (ConsensusRoundIdentifier tracked in _roundChangeCache.Keys)
        {
            lowestTrackedRound = System.Math.Min(lowestTrackedRound, tracked.Round);
        }

        if (lowestTrackedRound != int.MaxValue && lowestTrackedRound >= 2 && _logger.IsInfo)
        {
            _logger.Info($"BFT round summary (quorum = {quorum})");
            foreach ((Address address, ConsensusRoundIdentifier round) in _roundSummary)
            {
                _logger.Info($"Address: {address}  Round: {round.Round} {(address == localAddress ? "(Local node)" : "")}");
            }
        }
    }

    /// <summary>The lowest round above the current one that at least <c>f + 1</c> validators have moved to, if any.</summary>
    public int? FutureRoundChangeQuorumReceived(ConsensusRoundIdentifier currentRound)
    {
        int count = 0;
        int lowest = int.MaxValue;
        foreach (ConsensusRoundIdentifier round in _roundSummary.Values)
        {
            if (round.Round > currentRound.Round)
            {
                count++;
                lowest = System.Math.Min(lowest, round.Round);
            }
        }

        if (_logger.IsDebug) _logger.Debug($"Higher rounds size ={count} rcquorum = {futureRoundChangeQuorum}");
        return count >= futureRoundChangeQuorum && count > 0 ? lowest : null;
    }

    /// <summary>Stores a valid round change; returns the certificate when its target round just reached quorum.</summary>
    public IReadOnlyCollection<RoundChange>? AppendRoundChangeMessage(RoundChange message)
    {
        if (!RoundChangeMessageValidator.Validate(message))
        {
            if (_logger.IsInfo) _logger.Info("RoundChange message was invalid.");
            return null;
        }

        ConsensusRoundIdentifier target = message.RoundIdentifier;
        if (!_roundChangeCache.TryGetValue(target, out RoundChangeStatus? status))
        {
            status = new RoundChangeStatus(quorum);
            _roundChangeCache[target] = status;
        }

        status.AddMessage(message);
        return status.RoundChangeQuorumReceived ? status.CreateRoundChangeCertificate() : null;
    }

    public void DiscardRoundsPriorTo(ConsensusRoundIdentifier completedRound)
    {
        List<ConsensusRoundIdentifier> stale = [];
        foreach (ConsensusRoundIdentifier round in _roundChangeCache.Keys)
        {
            if (round.Round < completedRound.Round) stale.Add(round);
        }

        foreach (ConsensusRoundIdentifier round in stale) _roundChangeCache.Remove(round);
    }

    /// <summary>Round changes for one target round; one per validator, and the certificate is handed out only once.</summary>
    public sealed class RoundChangeStatus(long quorum)
    {
        private readonly Dictionary<Address, RoundChange> _received = [];
        private readonly List<RoundChange> _order = [];
        private bool _actioned;

        public IReadOnlyCollection<RoundChange> ReceivedMessages => _order;

        public void AddMessage(RoundChange message)
        {
            if (!_actioned && _received.TryAdd(message.Author, message))
            {
                _order.Add(message);
            }
        }

        public bool RoundChangeQuorumReceived => _received.Count >= quorum && !_actioned;

        public IReadOnlyCollection<RoundChange> CreateRoundChangeCertificate()
        {
            if (!RoundChangeQuorumReceived)
            {
                throw new System.InvalidOperationException("Unable to create RoundChangeCertificate at this time.");
            }

            _actioned = true;
            return _order;
        }
    }
}
