// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

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
        private readonly IPruningTrieStore _trieStore;
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
            IPruningTrieStore trieStore,
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
            _trieStore = trieStore;
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

            if (DateTime.UtcNow - _lastPruning < _minimumPruningDelay)
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
                else
                {
                    e.Status = PruningStatus.Starting;

                    _ = RunFullPruning(_processExitSource.Token);
                }
            }
        }

        private async Task WaitForMainChainChange(Func<OnUpdateMainChainArgs, bool> handler, CancellationToken cancellationToken)
        {
            await Wait.ForEventCondition<OnUpdateMainChainArgs>(
                cancellationToken,
                (h) => _blockTree.OnUpdateMainChain += h,
                (h) => _blockTree.OnUpdateMainChain -= h,
                (e) => e.WereProcessed && handler(e));
        }

        protected virtual async Task RunFullPruning(CancellationToken cancellationToken)
        {
            IPruningContext? pruningContext = null;

            // we don't want to start pruning in the middle of block processing, lets wait for new head.
            await WaitForMainChainChange((e) =>
            {
                if (_fullPruningDb.TryStartPruning(_pruningConfig.Mode.IsMemory(), out IPruningContext fromDbPruningContext))
                {
                    pruningContext = fromDbPruningContext;
                }

                return true;
            }, cancellationToken);

            if (pruningContext is null) return;

            try
            {
                await RunFullPruning(pruningContext, cancellationToken);
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("full pruning failed. ", e);
            }
            finally
            {
                pruningContext.Dispose();
            }
        }

        private async Task RunFullPruning(IPruningContext pruningContext, CancellationToken cancellationToken)
        {
            _trieStore.PersistCache(cancellationToken);

            long blockToWaitFor = 0;
            await WaitForMainChainChange((e) =>
            {
                if (e.Blocks.Count == 0) return false;

                blockToWaitFor = e.Blocks[^1].Number;
                if (_logger.IsInfo)
                    _logger.Info($"Full Pruning Ready to start: waiting for state {blockToWaitFor} to be ready.");
                return true;
            }, cancellationToken);

            await WaitForMainChainChange((e) =>
            {
                if (_blockTree.BestPersistedState >= blockToWaitFor) return true;
                if (_logger.IsInfo) _logger.Info($"Full Pruning Waiting for state: Current best saved finalized state {_blockTree.BestPersistedState}, waiting for state {blockToWaitFor} in order to not lose any cached state.");
                return false;
            }, cancellationToken);

            long stateToCopy = _blockTree.BestPersistedState.Value;
            long blockToPruneAfter = stateToCopy + Reorganization.MaxDepth;

            await WaitForMainChainChange((e) =>
            {
                if (_blockTree.Head?.Number > blockToPruneAfter) return true;
                if (_logger.IsInfo) _logger.Info($"Full Pruning Waiting for block: {blockToPruneAfter} in order to support reorganizations.");
                return false;
            }, cancellationToken);

            BlockHeader? header = _blockTree.FindHeader(stateToCopy);
            if (header is null)
            {
                if (_logger.IsError) _logger.Info($"Header for the state is missing");
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Full Pruning Ready to start: pruning garbage before state {stateToCopy} with root {header.StateRoot}");
            await CopyTrie(pruningContext, header.StateRoot!, cancellationToken);
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

        private async Task CopyTrie(IPruningContext pruning, Hash256 stateRoot, CancellationToken cancellationToken)
        {
            try
            {
                pruning.MarkStart();

                WriteFlags writeFlags = WriteFlags.DisableWAL;
                if (!_pruningConfig.FullPruningDisableLowPriorityWrites)
                {
                    writeFlags |= WriteFlags.LowPriority;
                }

                using CopyTreeVisitor copyTreeVisitor = new(pruning, writeFlags, _logManager, cancellationToken);
                VisitingOptions visitingOptions = new()
                {
                    MaxDegreeOfParallelism = _pruningConfig.FullPruningMaxDegreeOfParallelism,
                    FullScanMemoryBudget = ((long)_pruningConfig.FullPruningMemoryBudgetMb).MiB(),
                };
                if (_logger.IsInfo) _logger.Info($"Full pruning started with MaxDegreeOfParallelism: {visitingOptions.MaxDegreeOfParallelism} and FullScanMemoryBudget: {visitingOptions.FullScanMemoryBudget}");
                _stateReader.RunTreeVisitor(copyTreeVisitor, stateRoot, visitingOptions);

                if (!cancellationToken.IsCancellationRequested)
                {
                    copyTreeVisitor.Finish();

                    await WaitForMainChainChange((e) =>
                    {
                        // The db swap happens here. We do it within the event handler of main chain change to block
                        // so that it does not happen during block processing.
                        pruning.Commit();
                        return true;
                    }, cancellationToken);

                    _lastPruning = DateTime.UtcNow;
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error during pruning. ", e);
                throw;
            }
        }

        public void Dispose()
        {
            _pruningTrigger.Prune -= OnPrune;
            _fullPruningDb.PruningFinished -= HandlePruningFinished;
        }
    }
}
