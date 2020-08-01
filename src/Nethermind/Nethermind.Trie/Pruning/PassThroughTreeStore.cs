using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    public class PassThroughTreeStore : ITreeStore
    {
        private readonly ITrieNodeCache _trieNodeCache;
        private readonly IKeyValueStore _keyValueStore;
        private readonly ILogger _logger;

        public PassThroughTreeStore(IKeyValueStore keyValueStore, ILogManager logManager)
        {
            _trieNodeCache = new TrieNodeCache();
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
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
                
                if(_logger.IsTrace) _logger.Trace($"Storing {trieNode.Keccak} = {trieNode.FullRlp.ToHexString()}");
                _keyValueStore[trieNode.Keccak.Bytes] = trieNode.FullRlp;
            }
        }

        public void FinishBlockCommit(long blockNumber, TrieNode? root)
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
