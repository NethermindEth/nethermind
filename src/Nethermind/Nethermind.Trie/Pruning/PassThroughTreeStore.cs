using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class PassThroughTreeStore : ITreeStore
    {
        private readonly ITrieNodeCache _trieNodeCache;
        private readonly IKeyValueStore _keyValueStore;

        public PassThroughTreeStore(IKeyValueStore keyValueStore)
        {
            _trieNodeCache = new TrieNodeCache();
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
        }
        
        public void Commit(long blockNumber, TrieNode trieNode)
        {
            _keyValueStore[trieNode.Keccak!.Bytes] = trieNode.FullRlp;
        }

        public TrieNode FindCachedOrUnknown(Keccak hash)
        {
            return _trieNodeCache.Get(hash);
        }

        public byte[]? LoadRlp(Keccak hash, bool allowCaching)
        {
            return _keyValueStore[hash.Bytes];
        }

        public void UpdateRefs(TrieNode trieNode, int refChange)
        {
            throw new NotImplementedException();
        }
    }
}