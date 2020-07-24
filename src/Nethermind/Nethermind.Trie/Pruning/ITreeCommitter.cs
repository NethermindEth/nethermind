namespace Nethermind.Trie.Pruning
{
    public interface ITreeCommitter
    {
        void Commit(long blockNumber, TrieNode trieNode);

        void Uncommit();
        
        byte[] this[byte[] key] { get; }
    }
}