using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class NullTreeCommitter : ITreeCommitter
    {
        public void Commit(long blockNumber, TrieNode trieNode)
        {
        }

        public void Uncommit()
        {
        }

        public void Flush()
        {
        }

        public TrieNode FindCached(Keccak key)
        {
            return null;
        }

        public byte[] this[byte[] key] => null;
    }
}