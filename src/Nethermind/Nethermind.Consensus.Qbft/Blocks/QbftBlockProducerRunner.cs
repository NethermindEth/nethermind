// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.Blocks;

/// <summary>
/// Hosts the consensus loop: a single thread that drains the event queue into the controller, fed by
/// the network, the timers and new chain heads.
/// </summary>
/// <remarks>
/// The controller is paused while the node is syncing (head far behind the best suggested header) and
/// resumed on the first chain head that is within reach, mirroring Besu's <c>BftMiningCoordinator</c>
/// sync subscription. Blocks reach the chain through <see cref="QbftBlockImporter"/>, so
/// <see cref="BlockProduced"/> is never raised.
/// </remarks>
public sealed class QbftBlockProducerRunner(
    IBlockTree blockTree,
    BftEventQueue eventQueue,
    IBftEventHandler controller,
    ILogManager logManager) : IBlockProducerRunner, IDisposable
{
    /// <summary>Head-to-best-suggested distance beyond which the node is considered syncing and consensus is paused.</summary>
    public const int MaxSyncDistanceForConsensus = 8;

    private readonly ILogger _logger = logManager.GetClassLogger<QbftBlockProducerRunner>();
    private readonly BftEventMultiplexer _multiplexer = new(controller, logManager);
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _loopCompleted;
    private bool _controllerStarted;

    public event EventHandler<BlockEventArgs>? BlockProduced { add { } remove { } }

    public void Start()
    {
        lock (_lock)
        {
            if (_loopCompleted is not null) return;
            _cts = new CancellationTokenSource();
            eventQueue.Start();
            blockTree.NewHeadBlock += OnNewHeadBlock;
            TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _loopCompleted = completed;
            CancellationToken token = _cts.Token;
            // A dedicated thread: the loop creates blocks synchronously and must not occupy a pool thread while it does.
            Thread thread = new(() => RunLoop(token, completed)) { IsBackground = true, Name = "QBFT consensus" };
            thread.Start();
            eventQueue.Add(new SyncCheckEvent());
        }
    }

    public async Task StopAsync()
    {
        TaskCompletionSource? completed;
        lock (_lock)
        {
            blockTree.NewHeadBlock -= OnNewHeadBlock;
            eventQueue.Stop();
            _cts?.Cancel();
            completed = _loopCompleted;
            _loopCompleted = null;
        }

        if (completed is not null)
        {
            await completed.Task;
        }

        lock (_lock)
        {
            StopController();
            _cts?.Dispose();
            _cts = null;
        }
    }

    public bool IsProducingBlocks(ulong? maxProducingInterval)
    {
        lock (_lock) return _controllerStarted;
    }

    /// <summary>Whether the consensus loop thread is running; false once <see cref="StopAsync"/> has been called.</summary>
    internal bool IsLoopRunning
    {
        get
        {
            lock (_lock) return _loopCompleted is not null;
        }
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        eventQueue.Add(new SyncCheckEvent());
        eventQueue.Add(new NewChainHeadEvent(e.Block.Header));
    }

    private void RunLoop(CancellationToken token, TaskCompletionSource completed)
    {
        if (_logger.IsInfo) _logger.Info("QBFT consensus loop started");
        try
        {
            while (!token.IsCancellationRequested)
            {
                BftEvent? bftEvent = eventQueue.Read(token);
                if (bftEvent is null)
                {
                    break;
                }

                if (bftEvent is SyncCheckEvent)
                {
                    // Starting a height manager reads the chain and the validator contract, so it can throw;
                    // one failure must not fault the loop and leave the node with an unread event queue.
                    try
                    {
                        UpdateControllerForSyncState();
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsError) _logger.Error("Failed to update QBFT consensus for the current sync state", e);
                    }
                }
                else if (_controllerStarted)
                {
                    _multiplexer.HandleBftEvent(bftEvent);
                }
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("QBFT consensus loop stopped unexpectedly", e);
        }
        finally
        {
            blockTree.NewHeadBlock -= OnNewHeadBlock;
            if (_logger.IsInfo) _logger.Info("Shutting down QBFT consensus loop");
            completed.TrySetResult();
        }
    }

    private void UpdateControllerForSyncState()
    {
        (bool isSyncing, ulong headNumber, ulong bestSuggested) = blockTree.IsSyncing(MaxSyncDistanceForConsensus);
        bool genesisBootstrap = headNumber == 0 && bestSuggested == 0;
        bool shouldRun = blockTree.Head is not null && (!isSyncing || genesisBootstrap);
        lock (_lock)
        {
            if (shouldRun && !_controllerStarted)
            {
                if (_logger.IsInfo) _logger.Info("Starting QBFT consensus following sync");
                controller.Start();
                _controllerStarted = true;
            }
            else if (!shouldRun && _controllerStarted)
            {
                if (_logger.IsInfo) _logger.Info("Stopping QBFT consensus while we are syncing");
                StopController();
            }
        }
    }

    private void StopController()
    {
        if (_controllerStarted)
        {
            controller.Stop();
            _controllerStarted = false;
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    /// <summary>Internal event asking the loop to re-evaluate whether consensus should be running.</summary>
    private sealed record SyncCheckEvent : BftEvent;
}
