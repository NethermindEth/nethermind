using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class PassThroughTreeCommitter : ITreeCommitter
    {
        private readonly ITrieNodeCache _trieNodeCache;
        private readonly IKeyValueStore _keyValueStore;

        public PassThroughTreeCommitter(IKeyValueStore keyValueStore)
        {
            _trieNodeCache = new TrieNodeCache();
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
        }
        
        public void Commit(long blockNumber, TrieNode trieNode)
        {
            _keyValueStore[trieNode.Keccak!.Bytes] = trieNode.FullRlp;
        }

        public void Uncommit()
        {
        }

        public void Flush()
        {
        }

        public TrieNode FindCachedOrUnknown(Keccak hash)
        {
            return _trieNodeCache.Get(hash);
        }

        public byte[] this[byte[] key] => _keyValueStore[key];
    }
}