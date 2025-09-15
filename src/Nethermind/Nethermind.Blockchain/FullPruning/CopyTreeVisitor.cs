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
        private readonly ProgressLogger _progressLogger;
        private readonly INodeStorage _sourceNodeStorage;

        public CopyTreeVisitor(
            INodeStorage nodeStorage,
            WriteFlags writeFlags,
            ILogManager logManager,
            CancellationToken cancellationToken,
            ProgressLogger progressLogger)
        {
            _cancellationToken = cancellationToken;
            _writeFlags = writeFlags;
            _logger = logManager.GetClassLogger();
            _stopwatch = new Stopwatch();
            _concurrentWriteBatcher = new ConcurrentNodeWriteBatcher(nodeStorage);
            _progressLogger = progressLogger ?? throw new ArgumentNullException(nameof(progressLogger));
            _sourceNodeStorage = nodeStorage;
        }

        public bool IsFullDbScan => true;

        public ReadFlags ExtraReadFlag => ReadFlags.SkipDuplicateRead;

        public bool ShouldVisit(in TContext context, in ValueHash256 nextNode) => !_cancellationToken.IsCancellationRequested;

        public void VisitTree(in TContext nodeContext, in ValueHash256 rootHash)
        {
            _stopwatch.Start();
            if (_logger.IsInfo) _logger.Info($"Full Pruning Started on root hash {rootHash}: do not close the node until finished or progress will be lost.");

            // Initialize total nodes to process - we don't estimate since full pruning
            // may not copy all nodes and the actual count depends on pruning strategy
            _totalNodesToProcess = 0; // We'll track progress without a target

            // Initialize progress logger
            _progressLogger.Reset(0, _totalNodesToProcess);
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

                _progressLogger.Update(currentNodes);

                // Log progress every 1 million nodes or when progress logger suggests
                if (currentNodes % Million == 0)
                {
                    _progressLogger.LogProgress();
                }
            }
        }

        private void LogProgress(string state)
        {
            if (_logger.IsInfo)
            {
                var elapsed = _stopwatch.Elapsed;
                var nodesPerSecond = elapsed.TotalSeconds > 0 ? _persistedNodes / elapsed.TotalSeconds : 0;

                _logger.Info($"Full Pruning {state}: {elapsed} | " +
                           $"Nodes: {_persistedNodes:N0} | " +
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
            // Since we cannot accurately estimate the number of nodes that will be copied
            // during full pruning (as it depends on the pruning strategy and state structure),
            // we return 0 to indicate that we don't have a target value.
            // ProgressLogger will handle this by showing only current progress without percentage.
            return 0;
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

            _progressLogger.MarkEnd();
            _progressLogger.LogProgress();

            _concurrentWriteBatcher.Dispose();
        }
    }

    public interface ICopyTreeVisitor : IDisposable
    {
        void Finish();
    }
}
