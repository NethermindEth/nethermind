//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.Blockchain.FullPruning
{
    public class CopyTreeVisitor : ITreeVisitor, IDisposable
    {
        private readonly IPruningContext _pruningContext;
        private readonly CancellationToken _cancellationToken;
        private readonly ILogger _logger;
        private readonly Stopwatch _stopwatch;
        private long _persistedNodes = 0;
        private bool _finished = false;

        public CopyTreeVisitor(
            IPruningContext pruningContext, 
            CancellationToken cancellationToken,
            ILogManager logManager)
        {
            _pruningContext = pruningContext;
            _cancellationToken = cancellationToken;
            _logger = logManager.GetClassLogger();
            _stopwatch = new Stopwatch();
        }

        public bool ShouldVisit(Keccak nextNode) => !_cancellationToken.IsCancellationRequested;

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
        {
            _stopwatch.Start();
            if (_logger.IsWarn) _logger.Warn($"Full Pruning Started: do not close the node until finished or progress will be lost.");
        }


        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
        {
            // TODO: Cancel?
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext) => PersistNode(node);

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext) => PersistNode(node);

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null) => PersistNode(node);

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext) { }
        
        private void PersistNode(TrieNode node)
        {
            _pruningContext[node.Keccak!.Bytes] = node.FullRlp;
            _persistedNodes++;
            if (_logger.IsInfo && _persistedNodes % 1_000_000 == 0)
            {
                _logger.Info($"Full Pruning In Progress: {_stopwatch.Elapsed} {_persistedNodes} nodes persisted.");
            }
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
            
            if (_logger.IsInfo)
            {
                _logger.Info($"Full Pruning Finished: {_stopwatch.Elapsed} {_persistedNodes} nodes persisted.");
            }
        }
    }
}
