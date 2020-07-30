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
        
        public void Commit(long blockNumber, NodeCommitInfo nodeCommitInfo)
        {
            TrieNode? trieNode = nodeCommitInfo.Node;
            if (trieNode != null)
            {
                if (trieNode.Keccak is null)
                {
                    throw new ArgumentNullException($"Keccak is null when commiting a node {trieNode}");
                }

                _keyValueStore[trieNode.Keccak.Bytes] = trieNode.FullRlp;
            }
        }

        public void FinalizeBlock(long blockNumber, TrieNode? root)
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

        public void UpdateRefs(TrieNode trieNode, int refChange)
        {
            throw new NotImplementedException();
        }
    }
}
