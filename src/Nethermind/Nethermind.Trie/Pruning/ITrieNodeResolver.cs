using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ITrieNodeResolver
    {
        TrieNode FindCachedOrUnknown(Keccak hash);
        
        byte[] LoadRlp(Keccak hash, bool allowCaching);
    }
}