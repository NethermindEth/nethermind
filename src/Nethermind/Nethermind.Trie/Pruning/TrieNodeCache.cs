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
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    public class TrieNodeCache : ITrieNodeCache
    {
        public TrieNodeCache()
        {
            _logger = NullLogger.Instance;
        }
        
        public TrieNodeCache(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public int Count => _actualCache.Count;

        public bool IsInMemory(Keccak hash)
        {
            return _actualCache.ContainsKey(hash);
        }

        public TrieNode Get(Keccak hash)
        {
            if (!_actualCache.ContainsKey(hash))
            {
                if(_logger.IsTrace) _logger.Trace($"Creating new node {hash}");
                TrieNode newNode = new TrieNode(NodeType.Unknown, hash);
                _actualCache.Add(newNode.Keccak!, newNode);
            }
            
            return _actualCache[hash];
        }

        public void Set(Keccak hash, TrieNode trieNode)
        {
            _actualCache[hash] = trieNode;
        }

        public void Remove(Keccak hash)
        {
            _actualCache.Remove(hash);
        }

        public void Dump()
        {
            if (_logger.IsTrace)
            {
                _logger.Trace($"Trie node cache ({_actualCache.Count})");
                foreach (KeyValuePair<Keccak, TrieNode> keyValuePair in _actualCache)
                {
                    _logger.Trace($"  {keyValuePair.Value}");
                }
            }
        }

        public void Prune()
        {
            foreach ((Keccak key, TrieNode value) in _actualCache)
            {
                if (value.Refs == 0)
                {
                    if (!value.IsPersisted)
                    {
                        if (_logger.IsInfo) _logger.Info($"Pruning in cache: {value}.");
                    }

                    _actualCache.Remove(key);
                }
                
                if (value.IsPersisted)
                {
                    // TODO: remove refs
                    if(_logger.IsTrace) _logger.Trace($"Removing persisted {value} from memory.");
                    _actualCache.Remove(key);
                }
            }
        }

        #region private

        private ILogger _logger;
        
        private Dictionary<Keccak, TrieNode> _actualCache
            = new Dictionary<Keccak, TrieNode>();        

        #endregion
    }
}