using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface IRefsCache
    {
        TrieNode Get(Keccak hash);
        
        void Add(Keccak hash, TrieNode trieNode);
        
        void Remove(Keccak hash);
    }
}