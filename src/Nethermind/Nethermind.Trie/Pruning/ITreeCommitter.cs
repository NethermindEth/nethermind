using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ITreeCommitter
    {
        void Commit(long blockNumber, TrieNode trieNode);

        void Uncommit();

        void Flush();

        TrieNode FindCached(Keccak key);
        
        byte[] this[byte[] key] { get; }
    }
}