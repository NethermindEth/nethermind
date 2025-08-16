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
        private readonly INodeStorageFactory _nodeStorageFactory;
        private readonly INodeStorage _nodeStorage;
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
        private readonly ProgressLogger _progressLogger;

        public FullPruner(
            IFullPruningDb fullPruningDb,
            INodeStorageFactory nodeStorageFactory,
            INodeStorage mainNodeStorage,
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
            _nodeStorageFactory = nodeStorageFactory;
            _nodeStorage = mainNodeStorage;
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
            _progressLogger = new ProgressLogger("Full Pruning", logManager);

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

        private Task WaitForMainChainChange(Func<OnUpdateMainChainArgs, bool> handler, CancellationToken cancellationToken)
        {
            return Wait.ForEventCondition<OnUpdateMainChainArgs>(
                cancellationToken,
                (h) => _blockTree.OnUpdateMainChain += h,
                (h) => _blockTree.OnUpdateMainChain -= h,
                (e) => e.WereProcessed && handler(e));
        }

        protected virtual async Task RunFullPruning(CancellationToken cancellationToken)
        {
            IPruningContext? pruningContext = null;

            using (_trieStore.PrepareStableState(cancellationToken))
            {
                if (_fullPruningDb.TryStartPruning(_pruningConfig.Mode.IsMemory(), out IPruningContext fromDbPruningContext))
                {
                    pruningContext = fromDbPruningContext;
                }
            }

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
            long blockToPruneAfter = stateToCopy + _pruningConfig.PruningBoundary;

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

        private Task CopyTrie(IPruningContext pruning, Hash256 stateRoot, CancellationToken cancellationToken)
        {
            INodeStorage.KeyScheme originalKeyScheme = _nodeStorage.Scheme;
            ICopyTreeVisitor visitor = null;

            try
            {
                pruning.MarkStart();

                WriteFlags writeFlags = WriteFlags.DisableWAL;
                if (!_pruningConfig.FullPruningDisableLowPriorityWrites)
                {
                    writeFlags |= WriteFlags.LowPriority;
                }

                INodeStorage targetNodeStorage = _nodeStorageFactory.WrapKeyValueStore(pruning, usePreferredKeyScheme: true);

                if (originalKeyScheme == INodeStorage.KeyScheme.HalfPath && targetNodeStorage.Scheme == INodeStorage.KeyScheme.Hash)
                {
                    // Because of write on read duplication, we can't move from HalfPath to Hash scheme as some of the
                    // read key which are in HalfPath may be written to the new db. This cause a problem as Hash
                    // scheme can be started with some code not tracking path, which will be unable to read these HalfPath
                    // keys.
                    if (_logger.IsWarn) _logger.Warn($"Full pruning from from HalfPath key to Hash key is not supported. Switching to HalfPath key scheme.");
                    targetNodeStorage.Scheme = INodeStorage.KeyScheme.HalfPath;
                }

                if (originalKeyScheme == INodeStorage.KeyScheme.Hash && targetNodeStorage.Scheme == INodeStorage.KeyScheme.HalfPath)
                {
                    if (_logger.IsInfo) _logger.Info($"Converting from Hash key scheme to HalfPath key scheme");

                    if (_pruningConfig.FullPruningMemoryBudgetMb < 8000)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Full pruning memory budget is less than 8 GB. Full pruning from Hash to HalfPath require more memory budget for efficient copy. Consider increasing full pruning memory budget to at least 8GB.");
                    }
                }

                VisitingOptions visitingOptions = new()
                {
                    MaxDegreeOfParallelism = _pruningConfig.FullPruningMaxDegreeOfParallelism,
                    FullScanMemoryBudget = ((long)_pruningConfig.FullPruningMemoryBudgetMb).MiB(),
                };
                if (_logger.IsInfo) _logger.Info($"Full pruning started with MaxDegreeOfParallelism: {visitingOptions.MaxDegreeOfParallelism} and FullScanMemoryBudget: {visitingOptions.FullScanMemoryBudget}");

                // Initialize progress logger for trie copying
                _progressLogger.Reset(0, 0); // We'll update the target value when we know the total nodes

                visitor = targetNodeStorage.Scheme == INodeStorage.KeyScheme.Hash
                    ? CopyTree<NoopTreePathContextWithStorage>(stateRoot, targetNodeStorage, writeFlags, visitingOptions, cancellationToken)
                    : CopyTree<TreePathContextWithStorage>(stateRoot, targetNodeStorage, writeFlags, visitingOptions, cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                {
                    visitor.Finish();

                    using (_trieStore.PrepareStableState(cancellationToken))
                    {
                        pruning.Commit();
                    }

                    _nodeStorage.Scheme = targetNodeStorage.Scheme;
                    _lastPruning = DateTime.UtcNow;
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error during pruning. ", e);
                _nodeStorage.Scheme = originalKeyScheme;
                throw;
            }
            finally
            {
                visitor?.Dispose();
            }

            return Task.CompletedTask;
        }

        private ICopyTreeVisitor CopyTree<TContext>(
            Hash256 stateRoot,
            INodeStorage targetNodeStorage,
            WriteFlags writeFlags,
            VisitingOptions visitingOptions,
            CancellationToken cancellationToken
        ) where TContext : struct, ITreePathContextWithStorage, INodeContext<TContext>
        {
            CopyTreeVisitor<TContext> copyTreeVisitor = new(targetNodeStorage, writeFlags, _logManager, cancellationToken, _progressLogger);
            _stateReader.RunTreeVisitor(copyTreeVisitor, stateRoot, visitingOptions);
            return copyTreeVisitor;
        }

        public void Dispose()
        {
            _pruningTrigger.Prune -= OnPrune;
            _fullPruningDb.PruningFinished -= HandlePruningFinished;
        }
    }
}
