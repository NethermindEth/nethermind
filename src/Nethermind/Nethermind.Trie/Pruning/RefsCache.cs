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

        #region private

        private ILogger _logger;
        
        private Dictionary<Keccak, TrieNode> _actualCache
            = new Dictionary<Keccak, TrieNode>();        

        #endregion
    }
}