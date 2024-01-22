// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Blockchain.FullPruning
{
    /// <summary>
    /// Main orchestrator of Full Pruning.
    /// </summary>
    public class FullPruner : IDisposable
    {
        private readonly IFullPruningDb _fullPruningDb;
        private readonly IPruningTrigger _pruningTrigger;
        private readonly IPruningConfig _pruningConfig;
        private readonly IBlockTree _blockTree;
        private readonly IStateReader _stateReader;
        private readonly IProcessExitSource _processExitSource;
        private readonly ILogManager _logManager;
        private readonly IChainEstimations _chainEstimations;
        private readonly IDriveInfo? _driveInfo;
        private IPruningContext? _currentPruning;
        private int _waitingForBlockProcessed = 0;
        private int _waitingForStateReady = 0;
        private long _blockToWaitFor;
        private long _stateToCopy;
        private readonly ILogger _logger;
        private readonly TimeSpan _minimumPruningDelay;
        private DateTime _lastPruning = DateTime.MinValue;

        public FullPruner(
            IFullPruningDb fullPruningDb,
            IPruningTrigger pruningTrigger,
            IPruningConfig pruningConfig,
            IBlockTree blockTree,
            IStateReader stateReader,
            IProcessExitSource processExitSource,
            IChainEstimations chainEstimations,
            IDriveInfo? driveInfo,
            ILogManager logManager)
        {
            _fullPruningDb = fullPruningDb;
            _pruningTrigger = pruningTrigger;
            _pruningConfig = pruningConfig;
            _blockTree = blockTree;
            _stateReader = stateReader;
            _processExitSource = processExitSource;
            _logManager = logManager;
            _chainEstimations = chainEstimations;
            _driveInfo = driveInfo;
            _pruningTrigger.Prune += OnPrune;
            _logger = _logManager.GetClassLogger();
            _minimumPruningDelay = TimeSpan.FromHours(_pruningConfig.FullPruningMinimumDelayHours);

            if (_pruningConfig.FullPruningCompletionBehavior != FullPruningCompletionBehavior.None)
            {
                _fullPruningDb.PruningFinished += HandlePruningFinished;
            }
        }

        /// <summary>
        /// Is activated by pruning trigger, tries to start full pruning.
        /// </summary>
        private void OnPrune(object? sender, PruningTriggerEventArgs e)
        {
            // Lets assume pruning is in progress
            e.Status = PruningStatus.InProgress;

            if (DateTime.Now - _lastPruning < _minimumPruningDelay)
            {
                e.Status = PruningStatus.Delayed;
            }
            // If we are already pruning, we don't need to do anything
            else if (CanStartNewPruning())
            {
                // Check if we have enough disk space to run pruning
                if (!HaveEnoughDiskSpaceToRun() && _pruningConfig.AvailableSpaceCheckEnabled)
                {
                    e.Status = PruningStatus.NotEnoughDiskSpace;
                }
                // we mark that we are waiting for block (for thread safety)
                else if (Interlocked.CompareExchange(ref _waitingForBlockProcessed, 1, 0) == 0)
                {
                    // we don't want to start pruning in the middle of block processing, lets wait for new head.
                    _blockTree.OnUpdateMainChain += OnUpdateMainChain;
                    e.Status = PruningStatus.Starting;
                }
            }
        }

        private void OnUpdateMainChain(object? sender, OnUpdateMainChainArgs e)
        {
            if (!e.WereProcessed) return;
            if (CanStartNewPruning())
            {
                if (Interlocked.CompareExchange(ref _waitingForBlockProcessed, 0, 1) == 1)
                {
                    if (e.Blocks is not null && e.Blocks.Count > 0)
                    {
                        if (_fullPruningDb.TryStartPruning(_pruningConfig.Mode.IsMemory(), out IPruningContext pruningContext))
                        {
                            SetCurrentPruning(pruningContext);
                            if (Interlocked.CompareExchange(ref _waitingForStateReady, 1, 0) == 0)
                            {
                                Block lastBlock = e.Blocks[^1];
                                _blockToWaitFor = lastBlock.Number;
                                _stateToCopy = long.MaxValue;
                                if (_logger.IsInfo) _logger.Info($"Full Pruning Ready to start: waiting for state {lastBlock.Number} to be ready.");
                            }
                        }
                    }
                }
            }
            else if (_waitingForStateReady == 1)
            {
                if (_blockTree.BestPersistedState >= _blockToWaitFor && _currentPruning is not null)
                {
                    if (_stateToCopy == long.MaxValue)
                    {
                        _stateToCopy = _blockTree.BestPersistedState.Value;
                    }

                    long blockToPruneAfter = _stateToCopy + Reorganization.MaxDepth;
                    if (_blockTree.Head?.Number > blockToPruneAfter)
                    {
                        BlockHeader? header = _blockTree.FindHeader(_stateToCopy);
                        if (header is not null && Interlocked.CompareExchange(ref _waitingForStateReady, 0, 1) == 1)
                        {
                            if (_logger.IsInfo) _logger.Info($"Full Pruning Ready to start: pruning garbage before state {_stateToCopy} with root {header.StateRoot}");
                            Task.Run(() => RunPruning(_currentPruning, header.StateRoot!));
                            _blockTree.OnUpdateMainChain -= OnUpdateMainChain;
                        }
                    }
                    else
                    {
                        if (_logger.IsInfo) _logger.Info($"Full Pruning Waiting for block: {blockToPruneAfter} in order to support reorganizations.");
                    }
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info($"Full Pruning Waiting for state: Current best saved finalized state {_blockTree.BestPersistedState}, waiting for state {_blockToWaitFor} in order to not lose any cached state.");
                }
            }
            else
            {
                _blockTree.OnUpdateMainChain -= OnUpdateMainChain;
            }
        }

        private void SetCurrentPruning(IPruningContext pruningContext)
        {
            IPruningContext? oldPruning = Interlocked.Exchange(ref _currentPruning, pruningContext);
            if (oldPruning is not null)
            {
                Task.Run(() => oldPruning.Dispose());
            }
        }

        private bool CanStartNewPruning() => _fullPruningDb.CanStartPruning;

        private const long ChainSizeThresholdFactor = 130;

        private bool HaveEnoughDiskSpaceToRun()
        {
            long? currentChainSize = _chainEstimations.PruningSize;
            if (currentChainSize is null)
            {
                if (_logger.IsInfo) _logger.Info("Full Pruning: Chain size estimation is unavailable.");
                return true;
            }

            long available = _driveInfo?.AvailableFreeSpace ?? 0;
            long required = currentChainSize.Value * ChainSizeThresholdFactor / 100;
            if (available < required)
            {
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"Not enough disk space to run full pruning. Required {required / 1.GB()} GB. Have {available / 1.GB()} GB");
                return false;
            }
            return true;
        }

        private void HandlePruningFinished(object? sender, PruningEventArgs e)
        {
            switch (_pruningConfig.FullPruningCompletionBehavior)
            {
                case FullPruningCompletionBehavior.AlwaysShutdown:
                case FullPruningCompletionBehavior.ShutdownOnSuccess when e.Success:
                    if (_logger.IsInfo) _logger.Info($"Full Pruning completed {(e.Success ? "successfully" : "unsuccessfully")}, shutting down as requested in the configuration.");
                    _processExitSource.Exit(ExitCodes.Ok);
                    break;
            }
        }

        protected virtual void RunPruning(IPruningContext pruning, Hash256 statRoot)
        {
            try
            {
                pruning.MarkStart();

                WriteFlags writeFlags = WriteFlags.DisableWAL;
                if (!_pruningConfig.FullPruningDisableLowPriorityWrites)
                {
                    writeFlags |= WriteFlags.LowPriority;
                }

                using CopyTreeVisitor copyTreeVisitor = new(pruning, writeFlags, _logManager);
                VisitingOptions visitingOptions = new()
                {
                    MaxDegreeOfParallelism = _pruningConfig.FullPruningMaxDegreeOfParallelism,
                    FullScanMemoryBudget = ((long)_pruningConfig.FullPruningMemoryBudgetMb).MiB(),
                };
                if (_logger.IsInfo) _logger.Info($"Full pruning started with MaxDegreeOfParallelism: {visitingOptions.MaxDegreeOfParallelism} and FullScanMemoryBudget: {visitingOptions.FullScanMemoryBudget}");
                _stateReader.RunTreeVisitor(copyTreeVisitor, statRoot, visitingOptions);

                if (!pruning.CancellationTokenSource.IsCancellationRequested)
                {
                    void CommitOnNewBLock(object o, OnUpdateMainChainArgs e)
                    {
                        if (!e.WereProcessed) return;
                        _blockTree.OnUpdateMainChain -= CommitOnNewBLock;
                        // ReSharper disable AccessToDisposedClosure
                        pruning.Commit();
                        _lastPruning = DateTime.Now;
                        pruning.Dispose();
                        // ReSharper restore AccessToDisposedClosure
                    }

                    _blockTree.OnUpdateMainChain += CommitOnNewBLock;
                    copyTreeVisitor.Finish();
                }
                else
                {
                    pruning.Dispose();
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error during pruning. ", e);
                pruning.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _blockTree.OnUpdateMainChain -= OnUpdateMainChain;
            _pruningTrigger.Prune -= OnPrune;
            _currentPruning?.Dispose();
            _fullPruningDb.PruningFinished -= HandlePruningFinished;
        }
    }
}
