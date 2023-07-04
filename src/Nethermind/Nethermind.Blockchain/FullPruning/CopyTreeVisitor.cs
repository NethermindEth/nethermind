// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    public class CopyTreeVisitor : ITreeVisitor, IDisposable
    {
        private readonly IPruningContext _pruningContext;
        private readonly ILogger _logger;
        private readonly Stopwatch _stopwatch;
        private long _persistedNodes = 0;
        private bool _finished = false;
        private WriteFlags _writeFlags;
        private readonly CancellationToken _cancellationToken;
        private const int Million = 1_000_000;

        public CopyTreeVisitor(
            IPruningContext pruningContext,
            WriteFlags writeFlags,
            ILogManager logManager)
        {
            _pruningContext = pruningContext;
            _cancellationToken = pruningContext.CancellationTokenSource.Token;
            _writeFlags = writeFlags;
            _logger = logManager.GetClassLogger();
            _stopwatch = new Stopwatch();
        }

        public bool IsFullDbScan => true;
        public bool ShouldVisit(Keccak nextNode) => !_cancellationToken.IsCancellationRequested;

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
        {
            _stopwatch.Start();
            if (_logger.IsWarn) _logger.Warn($"Full Pruning Started on root hash {rootHash}: do not close the node until finished or progress will be lost.");
        }

        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"Full Pruning Failed: Missing node {nodeHash} at level {trieVisitContext.Level}.");
            }

            // if nodes are missing then state trie is not valid and we need to stop copying it
            _pruningContext.CancellationTokenSource.Cancel();
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext) => PersistNode(node);

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext) => PersistNode(node);

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[]? value = null) => PersistNode(node);

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext) { }

        private void PersistNode(TrieNode node)
        {
            if (node.Keccak is not null)
            {
                // simple copy of nodes RLP
                _pruningContext.Set(node.Keccak.Bytes, node.FullRlp, _writeFlags);
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
