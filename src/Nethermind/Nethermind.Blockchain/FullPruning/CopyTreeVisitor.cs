// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain.FullPruning
{
    /// <summary>
    /// Visits the state trie and copies the nodes to pruning context.
    /// </summary>
    /// <remarks>
    /// During visiting of the state trie at specified state root it copies the existing trie into <see cref="IPruningContext"/>.
    /// </remarks>
    public class CopyTreeVisitor : ITreeVisitor, IDisposable
    {
        private readonly ILogger _logger;
        private readonly Stopwatch _stopwatch;
        private long _persistedNodes = 0;
        private bool _finished = false;
        private readonly WriteFlags _writeFlags;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly CancellationToken _cancellationToken;
        private readonly INodeStorage _nodeStorage;
        private const int Million = 1_000_000;

        public CopyTreeVisitor(
            INodeStorage nodeStorage,
            CancellationTokenSource cancellationTokenSource,
            WriteFlags writeFlags,
            ILogManager logManager)
        {
            _nodeStorage = nodeStorage;
            _cancellationTokenSource = cancellationTokenSource;
            _cancellationToken = cancellationTokenSource.Token;
            _writeFlags = writeFlags;
            _logger = logManager.GetClassLogger();
            _stopwatch = new Stopwatch();
        }

        public bool IsFullDbScan => true;

        public ReadFlags ExtraReadFlag => ReadFlags.SkipDuplicateRead;

        public bool ShouldVisit(Hash256 nextNode) => !_cancellationToken.IsCancellationRequested;

        public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext)
        {
            _stopwatch.Start();
            if (_logger.IsWarn) _logger.Warn($"Full Pruning Started on root hash {rootHash}: do not close the node until finished or progress will be lost.");
        }

        public void VisitMissingNode(in TreePath path, Hash256 nodeHash, TrieVisitContext trieVisitContext)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"Full Pruning Failed: Missing node {nodeHash} at level {trieVisitContext.Level}.");
            }

            // if nodes are missing then state trie is not valid and we need to stop copying it
            _cancellationTokenSource.Cancel();
        }

        public void VisitBranch(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext) => PersistNode(path, node, trieVisitContext);

        public void VisitExtension(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext) => PersistNode(path, node, trieVisitContext);

        public void VisitLeaf(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext, byte[]? value = null) => PersistNode(path, node, trieVisitContext);

        public void VisitCode(in TreePath path, Hash256 codeHash, TrieVisitContext trieVisitContext) { }

        private void PersistNode(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (node.Keccak is not null)
            {
                // simple copy of nodes RLP
                _nodeStorage.Set(trieVisitContext.Storage, path, node.Keccak, node.FullRlp.ToArray(), _writeFlags);
                Interlocked.Increment(ref _persistedNodes);

                // log message every 1 mln nodes
                if (_persistedNodes % Million == 0)
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
            if (_logger.IsWarn && !_finished)
            {
                _logger.Warn($"Full Pruning Cancelled: Full pruning didn't finish, progress is lost.");
            }
        }

        public void Finish()
        {
            _finished = true;
            LogProgress("Finished");
        }
    }
}
