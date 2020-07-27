using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ITreeCommitter
    {
        void Commit(long blockNumber, TrieNode trieNode);

        TrieNode FindCached(Keccak hash);
        
        byte[] this[byte[] key] { get; }
    }
}