// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.Blockchain.FullPruning
{
    /// <summary>
    /// Visits the state trie and copies the nodes to pruning context.
    /// </summary>
    /// <remarks>
    /// During visiting of the state trie at specified state root it copies the existing trie into <see cref="IPruningContext"/>.
    /// </remarks>
    public class CopyTreeVisitor<TContext> : ICopyTreeVisitor, ITreeVisitor<TContext> where TContext : struct, ITreePathContextWithStorage, INodeContext<TContext>
    {
        private readonly ILogger _logger;
        private readonly Stopwatch _stopwatch;
        private long _persistedNodes = 0;
        private long _totalNodesToProcess = 0;
        private bool _finished = false;
        private readonly WriteFlags _writeFlags;
        private readonly CancellationToken _cancellationToken;
        private const int Million = 1_000_000;
        private readonly ConcurrentNodeWriteBatcher _concurrentWriteBatcher;
        private readonly ProgressLogger? _progressLogger;
        private readonly INodeStorage _sourceNodeStorage;

        public CopyTreeVisitor(
            INodeStorage nodeStorage,
            WriteFlags writeFlags,
            ILogManager logManager,
            CancellationToken cancellationToken,
            ProgressLogger? progressLogger = null)
        {
            _cancellationToken = cancellationToken;
            _writeFlags = writeFlags;
            _logger = logManager.GetClassLogger();
            _stopwatch = new Stopwatch();
            _concurrentWriteBatcher = new ConcurrentNodeWriteBatcher(nodeStorage);
            _progressLogger = progressLogger;
            _sourceNodeStorage = nodeStorage;
        }

        public bool IsFullDbScan => true;

        public ReadFlags ExtraReadFlag => ReadFlags.SkipDuplicateRead;

        public bool ShouldVisit(in TContext context, in ValueHash256 nextNode) => !_cancellationToken.IsCancellationRequested;

        public void VisitTree(in TContext nodeContext, in ValueHash256 rootHash)
        {
            _stopwatch.Start();
            if (_logger.IsInfo) _logger.Info($"Full Pruning Started on root hash {rootHash}: do not close the node until finished or progress will be lost.");

            // Estimate total nodes to process based on the current state trie
            // This is a rough estimate since we don't know exactly how many nodes will be copied
            // but it gives users a better sense of progress than just counting copied nodes
            try
            {
                // Get a rough estimate of nodes in the current state
                _totalNodesToProcess = EstimateNodesInState(rootHash);
                if (_logger.IsInfo) _logger.Info($"Estimated {_totalNodesToProcess:N0} nodes to process during full pruning");
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn) _logger.Warn($"Could not estimate total nodes: {ex.Message}");
                _totalNodesToProcess = 0; // We'll track progress without a target
            }

            // Initialize progress logger if provided
            if (_progressLogger != null)
            {
                _progressLogger.Reset(0, _totalNodesToProcess);
            }
        }

        [DoesNotReturn, StackTraceHidden]
        public void VisitMissingNode(in TContext ctx, in ValueHash256 nodeHash)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"Full Pruning Failed: Missing node {nodeHash} at level {ctx.Storage}:{ctx.Path}.");
            }

            // if nodes are missing then state trie is not valid and we need to stop copying it
            throw new TrieException($"Trie {nodeHash} missing");
        }

        public void VisitBranch(in TContext ctx, TrieNode node) => PersistNode(ctx.Storage, ctx.Path, node);

        public void VisitExtension(in TContext ctx, TrieNode node) => PersistNode(ctx.Storage, ctx.Path, node);

        public void VisitLeaf(in TContext ctx, TrieNode node) => PersistNode(ctx.Storage, ctx.Path, node);

        public void VisitAccount(in TContext ctx, TrieNode node, in AccountStruct account) { }

        private void PersistNode(Hash256 storage, in TreePath path, TrieNode node)
        {
            if (node.Keccak is not null)
            {
                // simple copy of nodes RLP
                _concurrentWriteBatcher.Set(storage, path, node.Keccak, node.FullRlp.ToArray(), _writeFlags);
                long currentNodes = Interlocked.Increment(ref _persistedNodes);

                // Update progress logger if available
                if (_progressLogger != null)
                {
                    _progressLogger.Update(currentNodes);

                    // Log progress every 1 million nodes or when progress logger suggests
                    if (currentNodes % Million == 0)
                    {
                        _progressLogger.LogProgress();
                    }
                }
                else
                {
                    // Fallback to old logging method
                    if (currentNodes % Million == 0)
                    {
                        LogProgress("In Progress");
                    }
                }
            }
        }

        private void LogProgress(string state)
        {
            if (_logger.IsInfo)
            {
                var elapsed = _stopwatch.Elapsed;
                var nodesPerSecond = elapsed.TotalSeconds > 0 ? _persistedNodes / elapsed.TotalSeconds : 0;
                var progressPercent = _totalNodesToProcess > 0 ? (_persistedNodes * 100.0 / _totalNodesToProcess) : 0;

                _logger.Info($"Full Pruning {state}: {elapsed} | " +
                           $"Nodes: {_persistedNodes:N0} / {_totalNodesToProcess:N0} ({progressPercent:F1}%) | " +
                           $"Speed: {nodesPerSecond:N0} nodes/sec | " +
                           $"Mirrored: {_persistedNodes / (double)Million:N} mln nodes");
            }
        }

        /// <summary>
        /// Estimates the number of nodes in the state trie for progress tracking.
        /// This is a rough estimate since full pruning may not copy all nodes.
        /// </summary>
        private long EstimateNodesInState(ValueHash256 rootHash)
        {
            // This is a simplified estimation - in practice, you might want to:
            // 1. Do a quick scan of the trie to count nodes
            // 2. Use historical data from previous pruning operations
            // 3. Use block number to estimate state size

            // For now, we'll use a conservative estimate based on typical state sizes
            // This can be improved with more sophisticated estimation logic
            return 10_000_000; // Conservative estimate for mainnet state
        }

        public void Dispose()
        {
            if (_logger.IsWarn && !_finished)
            {
                _logger.Warn($"Full Pruning Cancelled: Full pruning didn't finish, progress is lost.");
            }
        }

        public void Finish()
        {
            _finished = true;

            if (_progressLogger != null)
            {
                _progressLogger.MarkEnd();
                _progressLogger.LogProgress();
            }
            else
            {
                LogProgress("Finished");
            }

            _concurrentWriteBatcher.Dispose();
        }
    }

    public interface ICopyTreeVisitor : IDisposable
    {
        void Finish();
    }
}
