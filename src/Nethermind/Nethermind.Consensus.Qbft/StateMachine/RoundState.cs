// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.Validation;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>
/// Messages collected for one round: the accepted proposal and one prepare and commit per validator.
/// </summary>
/// <remarks>
/// Prepares and commits that arrive before the proposal are kept unvalidated and re-checked once the
/// proposal is known, so out-of-order delivery does not lose votes.
/// </remarks>
public sealed class RoundState(ConsensusRoundIdentifier roundIdentifier, int quorum, IMessageValidator validator, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<RoundState>();
    private readonly Dictionary<Address, Prepare> _prepares = [];
    private readonly Dictionary<Address, Commit> _commits = [];
    private readonly List<Address> _prepareOrder = [];
    private readonly List<Address> _commitOrder = [];
    private Proposal? _proposal;

    public ConsensusRoundIdentifier RoundIdentifier { get; } = roundIdentifier;
    public IMessageValidator Validator { get; } = validator;
    public bool IsPrepared { get; private set; }
    public bool IsCommitted { get; private set; }

    public Block? ProposedBlock => _proposal?.Block;
    public ReadOnlyBlockAccessList? ProposedBlockAccessList => _proposal?.BlockAccessList;

    /// <summary>Accepts the round's proposal if none was accepted yet and it validates; drops buffered votes that no longer validate.</summary>
    public bool SetProposedBlock(Proposal message)
    {
        if (_proposal is not null || !Validator.ValidateProposal(message))
        {
            return false;
        }

        _proposal = message;
        RemoveInvalid(_prepares, _prepareOrder, Validator.ValidatePrepare);
        RemoveInvalid(_commits, _commitOrder, Validator.ValidateCommit);
        UpdateState();
        return true;
    }

    public void AddPrepareMessage(Prepare message)
    {
        if (_proposal is null || Validator.ValidatePrepare(message))
        {
            if (_prepares.TryAdd(message.Author, message))
            {
                _prepareOrder.Add(message.Author);
            }

            if (_logger.IsTrace) _logger.Trace($"Round state added prepare message prepare={message}");
        }

        UpdateState();
    }

    public void AddCommitMessage(Commit message)
    {
        if (_proposal is null || Validator.ValidateCommit(message))
        {
            if (_commits.TryAdd(message.Author, message))
            {
                _commitOrder.Add(message.Author);
            }

            if (_logger.IsTrace) _logger.Trace($"Round state added commit message commit={message}");
        }

        UpdateState();
    }

    /// <summary>Committed seals in arrival order.</summary>
    public IReadOnlyList<Signature> CommitSeals
    {
        get
        {
            Signature[] seals = new Signature[_commitOrder.Count];
            for (int i = 0; i < _commitOrder.Count; i++)
            {
                seals[i] = _commits[_commitOrder[i]].CommitSeal;
            }

            return seals;
        }
    }

    public PreparedCertificate? ConstructPreparedCertificate()
    {
        if (!IsPrepared)
        {
            return null;
        }

        SignedData<PreparePayload>[] prepares = new SignedData<PreparePayload>[_prepareOrder.Count];
        for (int i = 0; i < _prepareOrder.Count; i++)
        {
            prepares[i] = _prepares[_prepareOrder[i]].SignedPayload;
        }

        return new PreparedCertificate(_proposal!.Block, prepares, RoundIdentifier.Round, _proposal.BlockAccessList);
    }

    private void UpdateState()
    {
        IsPrepared = _prepares.Count >= quorum && _proposal is not null;
        IsCommitted = _commits.Count >= quorum && _proposal is not null;
        if (_logger.IsTrace) _logger.Trace($"Round state updated prepared={IsPrepared} committed={IsCommitted} preparedQuorum={_prepares.Count}/{quorum} committedQuorum={_commits.Count}/{quorum}");
    }

    private static void RemoveInvalid<T>(Dictionary<Address, T> messages, List<Address> order, System.Func<T, bool> isValid)
    {
        for (int i = order.Count - 1; i >= 0; i--)
        {
            Address author = order[i];
            if (!isValid(messages[author]))
            {
                messages.Remove(author);
                order.RemoveAt(i);
            }
        }
    }
}
