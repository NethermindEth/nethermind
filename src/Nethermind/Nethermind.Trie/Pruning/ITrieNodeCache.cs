using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ITrieNodeCache
    {
        int Count { get; }
        
        bool IsInMemory(Keccak hash);
        
        TrieNode Get(Keccak hash);
        
        void Set(Keccak hash, TrieNode trieNode);
        
        void Remove(Keccak hash);
        
        void Dump();
        
        void Prune();
    }
}