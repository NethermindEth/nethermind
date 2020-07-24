using System;

namespace Nethermind.Trie.Pruning
{
    public class PassThroughTreeCommitter : ITreeCommitter
    {
        private readonly IKeyValueStore _keyValueStore;

        public PassThroughTreeCommitter(IKeyValueStore keyValueStore)
        {
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

        public byte[] this[byte[] key] => _keyValueStore[key];
    }
}