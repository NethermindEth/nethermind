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
using System.Timers;

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
        private bool _finished = false;
        private readonly WriteFlags _writeFlags;
        private readonly CancellationToken _cancellationToken;
        private const int Million = 1_000_000;
        private readonly ConcurrentNodeWriteBatcher _concurrentWriteBatcher;
        private readonly ProgressLogger _progressLogger;
        private readonly System.Timers.Timer _progressTimer;

        public CopyTreeVisitor(
            INodeStorage nodeStorage,
            WriteFlags writeFlags,
            ILogManager logManager,
            CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _writeFlags = writeFlags;
            _logger = logManager.GetClassLogger();
            _stopwatch = new Stopwatch();
            _concurrentWriteBatcher = new ConcurrentNodeWriteBatcher(nodeStorage);
            _progressLogger = new ProgressLogger("Full Pruning", logManager);
            _progressTimer = new System.Timers.Timer(1000); // Update every second
            _progressTimer.Elapsed += (_, _) => _progressLogger.LogProgress();
        }

        public bool IsFullDbScan => true;

        public ReadFlags ExtraReadFlag => ReadFlags.SkipDuplicateRead;

        public bool ShouldVisit(in TContext context, in ValueHash256 nextNode) => !_cancellationToken.IsCancellationRequested;

        public void VisitTree(in TContext nodeContext, in ValueHash256 rootHash)
        {
            _stopwatch.Start();
            if (_logger.IsInfo) _logger.Info($"Full Pruning Started on root hash {rootHash}: do not close the node until finished or progress will be lost.");

            // Reset and start progress logger with target value 0 initially (we don't know the total nodes yet)
            _progressLogger.Reset(0, 0);
            _progressLogger.SetFormat(formatter => $"Full Pruning | nodes: {formatter.CurrentValue:N0} | current: {formatter.CurrentPerSecond:N0} nodes/s | total: {formatter.TotalPerSecond:N0} nodes/s | elapsed: {_stopwatch.Elapsed}");
            _progressTimer.Start();
        }

        [DoesNotReturn]
        [StackTraceHidden]
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
                long nodes = Interlocked.Increment(ref _persistedNodes);

                // Update the progress logger with current node count
                _progressLogger.Update(nodes);

                // log message every 1 mln nodes
                if (nodes % Million == 0)
                {
                    LogProgress("In Progress");
                }
            }
        }

        private void LogProgress(string state)
        {
            if (_logger.IsInfo)
                _logger.Info($"Full Pruning {state}: {_stopwatch.Elapsed} {_persistedNodes / (double)Million:N} mln nodes mirrored.");
        }

        public void Dispose()
        {
            _progressTimer.Stop();
            _progressTimer.Dispose();

            if (_logger.IsWarn && !_finished)
            {
                _logger.Warn($"Full Pruning Cancelled: Full pruning didn't finish, progress is lost.");
            }
        }

        public void Finish()
        {
            _finished = true;
            _progressTimer.Stop();

            // Log final progress
            _progressLogger.SetMeasuringPoint();
            _progressLogger.LogProgress();
            _progressLogger.MarkEnd();

            LogProgress("Finished");
            _concurrentWriteBatcher.Dispose();
        }
    }

    public interface ICopyTreeVisitor : IDisposable
    {
        void Finish();
    }
}
