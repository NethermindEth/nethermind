// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Consensus.Qbft.Validation;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>Receives every consensus input; implemented by <see cref="QbftController"/>.</summary>
public interface IBftEventHandler
{
    void Start();
    void Stop();
    void HandleMessageEvent(ReceivedMessageEvent message);
    void HandleNewBlockEvent(NewChainHeadEvent newChainHead);
    void HandleBlockTimerExpiry(BlockTimerExpiryEvent blockTimerExpiry);
    void HandleRoundExpiry(RoundExpiryEvent roundExpiry);
}

/// <summary>
/// Top of the state machine: routes decoded messages and timer events to the height manager for the
/// chain head's child, buffers messages for future heights and gossips accepted messages.
/// </summary>
/// <remarks>Mirrors Besu's <c>QbftController</c>. Must only be driven from the single consensus thread.</remarks>
public sealed class QbftController(
    IBlockTree blockTree,
    IQbftFinalState finalState,
    QbftBlockHeightManagerFactory heightManagerFactory,
    IQbftGossiper gossiper,
    MessageTracker duplicateMessageTracker,
    FutureMessageBuffer futureMessageBuffer,
    QbftMessageCodec codec,
    ILogManager logManager) : IBftEventHandler
{
    private readonly ILogger _logger = logManager.GetClassLogger<QbftController>();
    private int _started;
    private IBlockHeightManager? _currentHeightManager;

    public long CurrentChainHeight => CurrentHeightManager.ChainHeight;

    public IMessageValidator? CurrentMessageValidator => CurrentHeightManager.CurrentRound?.RoundState.Validator;

    public RoundChangeMessageValidator? CurrentRoundChangeMessageValidator => CurrentHeightManager.RoundChangeManager?.RoundChangeMessageValidator;

    /// <summary>The round the node is currently in, or null when idle or not a validator.</summary>
    public ConsensusRoundIdentifier? CurrentRoundIdentifier => CurrentHeightManager.CurrentRound?.RoundIdentifier;

    private IBlockHeightManager CurrentHeightManager => _currentHeightManager ?? throw new InvalidOperationException("QBFT controller has not been started.");

    private BlockHeader ChainHead => blockTree.Head?.Header ?? throw new InvalidOperationException("Block tree has no head.");

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            // A caller that stopped the height manager (e.g. while sync completes) must call Stop() before starting again.
            throw new InvalidOperationException("Attempt to start new height manager without stopping previous manager");
        }

        StartNewHeightManager(ChainHead);
    }

    public void Stop()
    {
        if (Interlocked.CompareExchange(ref _started, 0, 1) == 1)
        {
            _currentHeightManager = heightManagerFactory.CreateNoOpBlockHeightManager(ChainHead);
            if (_logger.IsDebug) _logger.Debug("QBFT height manager stop");
        }
    }

    public void HandleMessageEvent(ReceivedMessageEvent message)
    {
        QbftReceivedMessage raw = message.Message;
        if (duplicateMessageTracker.HasSeenMessage(raw.Data))
        {
            if (_logger.IsTrace) _logger.Trace("Discarded duplicate message");
            return;
        }

        duplicateMessageTracker.AddSeenMessage(raw.Data);
        HandleMessage(raw);
    }

    private void HandleMessage(QbftReceivedMessage raw)
    {
        BftMessage message;
        try
        {
            message = codec.Decode(raw.Code, raw.Data);
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Discarding undecodable {QbftMessageCode.Name(raw.Code)} message from {raw.SenderAddress}: {e.Message}");
            return;
        }

        switch (message)
        {
            case Proposal proposal:
                ConsumeMessage(raw, proposal, CurrentHeightManager.HandleProposalPayload);
                break;
            case Prepare prepare:
                ConsumeMessage(raw, prepare, CurrentHeightManager.HandlePreparePayload);
                break;
            case Commit commit:
                ConsumeMessage(raw, commit, CurrentHeightManager.HandleCommitPayload);
                break;
            case RoundChange roundChange:
                ConsumeMessage(raw, roundChange, CurrentHeightManager.HandleRoundChangePayload);
                break;
        }
    }

    private void ConsumeMessage<T>(QbftReceivedMessage raw, T message, Action<T> handle) where T : BftMessage
    {
        if (_logger.IsTrace) _logger.Trace($"Received BFT {typeof(T).Name} message");
        // Messages at or below the chain head are stale: the head manager is one above it, except right after an import.
        long headNumber = (long)ChainHead.Number;
        if (message.RoundIdentifier.Sequence <= headNumber)
        {
            if (_logger.IsDebug) _logger.Debug($"Discarding a message which targets a height {message.RoundIdentifier.Sequence} not above current chain height {headNumber}.");
            return;
        }

        if (ProcessMessage(message, raw))
        {
            gossiper.Send(raw, message.Author);
            handle(message);
        }
    }

    private bool ProcessMessage(BftMessage message, QbftReceivedMessage raw)
    {
        long sequence = message.RoundIdentifier.Sequence;
        long currentHeight = CurrentChainHeight;
        if (sequence == currentHeight)
        {
            return finalState.Validators.ContainsAddress(message.Author) && finalState.IsLocalNodeValidator;
        }

        if (sequence > currentHeight)
        {
            if (_logger.IsTrace) _logger.Trace($"Received message for future block height round={message.RoundIdentifier}");
            futureMessageBuffer.AddMessage(sequence, raw);
        }
        else if (_logger.IsTrace)
        {
            _logger.Trace($"BFT message discarded as it is from a previous block height messageType={message.MessageType} chainHeight={currentHeight} eventHeight={sequence}");
        }

        return false;
    }

    public void HandleNewBlockEvent(NewChainHeadEvent newChainHead)
    {
        BlockHeader newHead = newChainHead.NewChainHeadHeader;
        BlockHeader currentParent = CurrentHeightManager.ParentBlockHeader;
        if (_logger.IsDebug) _logger.Debug($"New chain head detected (block number={newHead.Number}), currently mining on top of {currentParent.Number}.");

        if (newHead.Number < currentParent.Number)
        {
            if (_logger.IsTrace) _logger.Trace($"Discarding NewChainHead event, was for previous block height. chainHeight={currentParent.Number} eventHeight={newHead.Number}");
            return;
        }

        if (newHead.Number == currentParent.Number)
        {
            if (newHead.Hash == currentParent.Hash)
            {
                if (_logger.IsTrace) _logger.Trace($"Discarding duplicate NewChainHead event. chainHeight={newHead.Number} newBlockHash={newHead.Hash} parentBlockHash={currentParent.Hash}");
            }
            else if (_logger.IsError)
            {
                _logger.Error($"Subsequent NewChainHead event at same block height indicates chain fork. chainHeight={currentParent.Number}");
            }

            return;
        }

        StartNewHeightManager(newHead);
    }

    public void HandleBlockTimerExpiry(BlockTimerExpiryEvent blockTimerExpiry)
    {
        ConsensusRoundIdentifier round = blockTimerExpiry.RoundIdentifier;
        // The block may have been imported through sync while the timer was pending.
        if (round.Sequence <= (long)ChainHead.Number)
        {
            if (_logger.IsDebug) _logger.Debug("Discarding a block-timer which targets a height not above current chain height.");
            return;
        }

        if (round.Sequence == CurrentChainHeight)
        {
            CurrentHeightManager.HandleBlockTimerExpiry(round);
        }
        else if (_logger.IsTrace)
        {
            _logger.Trace($"Block timer event discarded as it is not for current block height chainHeight={CurrentChainHeight} eventHeight={round.Sequence}");
        }
    }

    public void HandleRoundExpiry(RoundExpiryEvent roundExpiry)
    {
        if (roundExpiry.View.Sequence <= (long)ChainHead.Number)
        {
            if (_logger.IsDebug) _logger.Debug("Discarding a round-expiry which targets a height not above current chain height.");
            return;
        }

        if (roundExpiry.View.Sequence == CurrentChainHeight)
        {
            CurrentHeightManager.RoundExpired(roundExpiry);
        }
        else if (_logger.IsTrace)
        {
            _logger.Trace($"Round expiry event discarded as it is not for current block height chainHeight={CurrentChainHeight} eventHeight={roundExpiry.View.Sequence}");
        }
    }

    private void StartNewHeightManager(BlockHeader parentHeader)
    {
        _currentHeightManager = heightManagerFactory.Create(parentHeader);
        foreach (QbftReceivedMessage buffered in futureMessageBuffer.RetrieveMessagesForHeight(CurrentChainHeight))
        {
            HandleMessage(buffered);
        }
    }
}

/// <summary>Dispatches queued events to the handler, isolating the loop from handler failures.</summary>
public sealed class BftEventMultiplexer(IBftEventHandler eventHandler, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<BftEventMultiplexer>();

    public void HandleBftEvent(BftEvent bftEvent)
    {
        try
        {
            switch (bftEvent)
            {
                case ReceivedMessageEvent message:
                    eventHandler.HandleMessageEvent(message);
                    break;
                case RoundExpiryEvent roundExpiry:
                    eventHandler.HandleRoundExpiry(roundExpiry);
                    break;
                case NewChainHeadEvent newChainHead:
                    eventHandler.HandleNewBlockEvent(newChainHead);
                    break;
                case BlockTimerExpiryEvent blockTimerExpiry:
                    eventHandler.HandleBlockTimerExpiry(blockTimerExpiry);
                    break;
                default:
                    throw new InvalidOperationException($"Illegal event in queue: {bftEvent}");
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"State machine threw exception while processing event {bftEvent}", e);
        }
    }
}
