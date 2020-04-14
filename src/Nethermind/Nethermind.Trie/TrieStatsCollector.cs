//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie
{
    public class TrieStatsCollector : ITreeVisitor
    {
        private readonly IKeyValueStore _codeKeyValueStore;
        private int _lastAccountNodeCount = 0;

        private readonly ILogger _logger;

        public TrieStatsCollector(IKeyValueStore codeKeyValueStore, ILogManager logManager)
        {
            _codeKeyValueStore = codeKeyValueStore ?? throw new ArgumentNullException(nameof(codeKeyValueStore));
            _logger = logManager.GetClassLogger();
        }

        public TrieStats Stats { get; } = new TrieStats();

        public bool ShouldVisit(Keccak nextNode)
        {
            return true;
        }

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Stats.MissingStorage++;
            }
            else
            {
                Stats.MissingState++;
            }
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Stats.StorageBranchCount++;
            }
            else
            {
                Stats.StateBranchCount++;
            }
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Stats.StorageExtensionCount++;
            }
            else
            {
                Stats.StateExtensionCount++;
            }
        }

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null)
        {
            if (Stats.NodesCount - _lastAccountNodeCount > 100000)
            {
                _lastAccountNodeCount = Stats.NodesCount;
                _logger.Warn($"Collected info from {Stats.NodesCount} nodes. Missing CODE {Stats.MissingCode} STATE {Stats.MissingState} STORAGE {Stats.MissingStorage}");
            }

            if (trieVisitContext.IsStorage)
            {
                Stats.StorageLeafCount++;
            }
            else
            {
                Stats.AccountCount++;
            }
        }

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
        {
            byte[] code = _codeKeyValueStore[codeHash.Bytes];
            if (code != null)
            {
                Stats.CodeCount++;
            }
            else
            {
                Stats.MissingCode++;
            }
        }
    }
}