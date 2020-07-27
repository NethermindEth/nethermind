namespace Nethermind.Trie.Pruning
{
    public interface ITreeStore : ITrieNodeResolver
    {
        void Commit(long blockNumber, TrieNode trieNode);
        
        void UpdateRefs(TrieNode trieNode, int refChange);
    }
}