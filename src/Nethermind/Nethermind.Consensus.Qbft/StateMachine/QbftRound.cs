// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>
/// One attempt at agreeing the next block: proposes (if proposer), prepares, commits and finally
/// imports the block once a commit quorum is reached.
/// </summary>
/// <remarks>Mirrors Besu's <c>QbftRound</c>; the round timer starts on construction.</remarks>
public sealed class QbftRound
{
    private readonly IQbftBlockCreator _blockCreator;
    private readonly BftBlockInterface _blockInterface;
    private readonly IQbftBlockImporter _blockImporter;
    private readonly IReadOnlyList<IMinedBlockObserver> _observers;
    private readonly MessageFactory _messageFactory;
    private readonly QbftMessageTransmitter _transmitter;
    private readonly BlockHeader _parentHeader;
    private readonly ILogger _logger;

    public QbftRound(
        RoundState roundState,
        IQbftBlockCreator blockCreator,
        BftBlockInterface blockInterface,
        IQbftBlockImporter blockImporter,
        IReadOnlyList<IMinedBlockObserver> observers,
        MessageFactory messageFactory,
        QbftMessageTransmitter transmitter,
        RoundTimer roundTimer,
        BlockHeader parentHeader,
        ILogManager logManager)
    {
        RoundState = roundState;
        _blockCreator = blockCreator;
        _blockInterface = blockInterface;
        _blockImporter = blockImporter;
        _observers = observers;
        _messageFactory = messageFactory;
        _transmitter = transmitter;
        _parentHeader = parentHeader;
        _logger = logManager.GetClassLogger<QbftRound>();
        roundTimer.StartTimer(RoundIdentifier);
    }

    public RoundState RoundState { get; }
    public ConsensusRoundIdentifier RoundIdentifier => RoundState.RoundIdentifier;

    public BlockCreationResult CreateBlock(ulong headerTimestampSeconds)
    {
        if (_logger.IsDebug) _logger.Debug($"Creating proposed block. round={RoundIdentifier}");
        return _blockCreator.CreateBlock(headerTimestampSeconds, _parentHeader);
    }

    /// <summary>Proposer entry after a round-change quorum: re-propose the best prepared block or build a new one.</summary>
    public void StartRoundWith(RoundChangeArtifacts artifacts, ulong headerTimestampSeconds)
    {
        PreparedCertificate? best = artifacts.BestPreparedPeer;
        Block blockToPublish;
        ReadOnlyBlockAccessList? blockAccessList;
        if (best is null)
        {
            if (_logger.IsDebug) _logger.Debug($"Sending proposal with new block. round={RoundIdentifier}");
            BlockCreationResult created = _blockCreator.CreateBlock(headerTimestampSeconds, _parentHeader);
            blockToPublish = created.Block;
            blockAccessList = created.BlockAccessList;
        }
        else
        {
            if (_logger.IsDebug) _logger.Debug($"Sending proposal from PreparedCertificate. round={RoundIdentifier}");
            blockToPublish = _blockInterface.ReplaceRound(best.Block, RoundIdentifier.Round);
            blockAccessList = best.BlockAccessList;
        }

        UpdateStateWithProposalAndTransmit(blockToPublish, blockAccessList, artifacts.RoundChanges, best?.Prepares ?? []);
    }

    public void UpdateStateWithProposalAndTransmit(
        Block block,
        ReadOnlyBlockAccessList? blockAccessList,
        IReadOnlyList<SignedData<RoundChangePayload>> roundChanges,
        IReadOnlyList<SignedData<PreparePayload>> prepares)
    {
        Proposal proposal;
        try
        {
            proposal = _messageFactory.CreateProposal(RoundIdentifier, block, blockAccessList, roundChanges, prepares);
        }
        catch (InvalidOperationException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to create a signed Proposal, waiting for next round. {e.Message}");
            return;
        }

        _transmitter.MulticastProposal(proposal);
        if (UpdateStateWithProposedBlock(proposal))
        {
            SendPrepare(block);
        }
    }

    public void HandleProposalMessage(Proposal message)
    {
        if (_logger.IsDebug) _logger.Debug($"Received a proposal message. round={RoundIdentifier}. author={message.Author}");
        if (UpdateStateWithProposedBlock(message))
        {
            SendPrepare(message.Block);
        }
    }

    public void HandlePrepareMessage(Prepare message)
    {
        if (_logger.IsDebug) _logger.Debug($"Received a prepare message. round={RoundIdentifier}. author={message.Author}");
        PeerIsPrepared(message);
    }

    public void HandleCommitMessage(Commit message)
    {
        if (_logger.IsDebug) _logger.Debug($"Received a commit message. round={RoundIdentifier}. author={message.Author}");
        PeerIsCommitted(message);
    }

    public PreparedCertificate? ConstructPreparedCertificate() => RoundState.ConstructPreparedCertificate();

    private void SendPrepare(Block block)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending prepare message. round={RoundIdentifier}");
        try
        {
            Prepare localPrepare = _messageFactory.CreatePrepare(RoundIdentifier, Digest(block));
            PeerIsPrepared(localPrepare);
            _transmitter.MulticastPrepare(localPrepare);
        }
        catch (InvalidOperationException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to create a signed Prepare; {e.Message}");
        }
    }

    private bool UpdateStateWithProposedBlock(Proposal message)
    {
        bool wasPrepared = RoundState.IsPrepared;
        bool wasCommitted = RoundState.IsCommitted;
        bool accepted = RoundState.SetProposedBlock(message);
        if (!accepted)
        {
            return false;
        }

        Block block = RoundState.ProposedBlock!;
        Signature commitSeal;
        try
        {
            commitSeal = _messageFactory.CreateCommitSeal(block, RoundIdentifier.Round);
        }
        catch (InvalidOperationException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to construct commit seal; {e.Message}");
            return true;
        }

        // Handling the proposal can complete a prepare quorum when prepares arrived first.
        if (wasPrepared != RoundState.IsPrepared)
        {
            if (_logger.IsDebug) _logger.Debug($"Sending commit message. round={RoundIdentifier}");
            _transmitter.MulticastCommit(RoundIdentifier, Digest(block), commitSeal);
        }

        // Our own commit can be recorded now; our prepare cannot, as the proposal may be ours and a proposer does not also prepare.
        try
        {
            Commit localCommit = _messageFactory.CreateCommit(RoundIdentifier, Digest(block), commitSeal);
            RoundState.AddCommitMessage(localCommit);
        }
        catch (InvalidOperationException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to create signed Commit message; {e.Message}");
            return true;
        }

        if (wasCommitted != RoundState.IsCommitted)
        {
            ImportBlockToChain();
        }

        return true;
    }

    private void PeerIsPrepared(Prepare message)
    {
        bool wasPrepared = RoundState.IsPrepared;
        RoundState.AddPrepareMessage(message);
        if (wasPrepared != RoundState.IsPrepared)
        {
            if (_logger.IsDebug) _logger.Debug($"Sending commit message. round={RoundIdentifier}");
            Block block = RoundState.ProposedBlock!;
            try
            {
                // The local commit was recorded when the proposal was accepted, so only the transmission is left.
                _transmitter.MulticastCommit(RoundIdentifier, Digest(block), _messageFactory.CreateCommitSeal(block, RoundIdentifier.Round));
            }
            catch (InvalidOperationException e)
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to construct a commit seal: {e.Message}");
            }
        }
    }

    private void PeerIsCommitted(Commit message)
    {
        bool wasCommitted = RoundState.IsCommitted;
        RoundState.AddCommitMessage(message);
        if (wasCommitted != RoundState.IsCommitted)
        {
            ImportBlockToChain();
        }
    }

    private void ImportBlockToChain()
    {
        Block sealed_ = _blockInterface.CreateSealedBlock(RoundState.ProposedBlock!, RoundIdentifier.Round, RoundState.CommitSeals);
        if (RoundIdentifier.Round > 0)
        {
            if (_logger.IsInfo) _logger.Info($"Importing proposed block to chain. round={RoundIdentifier}, hash={sealed_.Hash}");
        }
        else if (_logger.IsDebug)
        {
            _logger.Debug($"Importing proposed block to chain. round={RoundIdentifier}, hash={sealed_.Hash}");
        }

        if (!_blockImporter.ImportBlock(sealed_, RoundState.ProposedBlockAccessList))
        {
            if (_logger.IsError) _logger.Error($"Failed to import proposed block to chain. block={sealed_.Number} blockHeader={sealed_.Header.ToString(BlockHeader.Format.Short)}");
            return;
        }

        foreach (IMinedBlockObserver observer in _observers)
        {
            observer.BlockMined(sealed_);
        }
    }

    private Hash256 Digest(Block block) => new(_blockInterface.Digest(block));
}
