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
    private Task? _loop;
    private bool _controllerStarted;

    public event EventHandler<BlockEventArgs>? BlockProduced { add { } remove { } }

    public void Start()
    {
        lock (_lock)
        {
            if (_loop is not null) return;
            _cts = new CancellationTokenSource();
            eventQueue.Start();
            blockTree.NewHeadBlock += OnNewHeadBlock;
            _loop = Task.Factory.StartNew(() => RunLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            eventQueue.Add(new SyncCheckEvent());
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_lock)
        {
            blockTree.NewHeadBlock -= OnNewHeadBlock;
            eventQueue.Stop();
            _cts?.Cancel();
            loop = _loop;
            _loop = null;
        }

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException) { }
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

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        eventQueue.Add(new SyncCheckEvent());
        eventQueue.Add(new NewChainHeadEvent(e.Block.Header));
    }

    private async Task RunLoop(CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info("QBFT consensus loop started");
        while (!token.IsCancellationRequested)
        {
            BftEvent? bftEvent = await eventQueue.ReadAsync(token);
            if (bftEvent is null)
            {
                break;
            }

            if (bftEvent is SyncCheckEvent)
            {
                UpdateControllerForSyncState();
            }
            else if (_controllerStarted)
            {
                _multiplexer.HandleBftEvent(bftEvent);
            }
        }

        if (_logger.IsInfo) _logger.Info("Shutting down QBFT consensus loop");
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
