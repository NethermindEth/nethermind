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
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    public class PassThroughTrieStore : ITrieStore
    {
        private readonly ITrieNodeCache _trieNodeCache;
        private readonly IKeyValueStore _keyValueStore;
        private readonly ILogger _logger;

        public PassThroughTrieStore(IKeyValueStore keyValueStore, ILogManager logManager)
        {
            _trieNodeCache = new TrieNodeCache();
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public void Commit(TrieType trieType, long blockNumber, NodeCommitInfo nodeCommitInfo)
        {
            TrieNode? trieNode = nodeCommitInfo.Node;
            if (trieNode != null)
            {
                if (trieNode.Keccak is null)
                {
                    throw new ArgumentNullException($"Keccak is null when commiting a node {trieNode}");
                }
                
                if(_logger.IsTrace) _logger.Trace($"Storing {trieNode.Keccak} = {trieNode.FullRlp.ToHexString()}");
                _keyValueStore[trieNode.Keccak.Bytes] = trieNode.FullRlp;
            }
        }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root)
        {
        }

        public TrieNode FindCachedOrUnknown(Keccak hash)
        {
            return _trieNodeCache.Get(hash);
        }

        public byte[]? LoadRlp(Keccak hash, bool allowCaching)
        {
            return _keyValueStore[hash.Bytes];
        }

        public void Unwind()
        {
        }

        public event EventHandler<BlockNumberEventArgs> Stored;
    }
}
