using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    public class RefsCache : IRefsCache
    {
        public RefsCache(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
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

        public void Add(Keccak hash, TrieNode trieNode)
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