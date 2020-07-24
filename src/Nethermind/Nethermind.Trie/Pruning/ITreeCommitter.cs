namespace Nethermind.Trie.Pruning
{
    public interface ITreeCommitter
    {
        void Commit(long blockNumber, TrieNode trieNode);

        void Uncommit();

        void Flush();
        
        byte[] this[byte[] key] { get; }
    }
}