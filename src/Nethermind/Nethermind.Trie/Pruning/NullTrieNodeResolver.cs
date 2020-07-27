using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class NullTrieNodeResolver : ITrieNodeResolver
    {
        private NullTrieNodeResolver()
        {
            
        }
        
        public static NullTrieNodeResolver Instance = new NullTrieNodeResolver();
        
        public TrieNode FindCachedOrUnknown(Keccak hash)
        {
            return new TrieNode(NodeType.Unknown, hash);
        }

        public byte[]? LoadRlp(Keccak hash, bool allowCaching)
        {
            return null;
        }
    }
}