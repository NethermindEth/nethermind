using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class RefsCache : IRefsCache
    {
        private Dictionary<Keccak, TrieNode> _actualCache
            = new Dictionary<Keccak, TrieNode>();
        
        public TrieNode Get(Keccak hash)
        {
            if (!_actualCache.ContainsKey(hash))
            {
                return new TrieNode(NodeType.Unknown, hash);
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
    }
}