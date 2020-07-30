namespace Nethermind.Trie.Pruning
{
    public interface ITreeStore : ITrieNodeResolver
    {
        void Commit(long blockNumber, NodeCommitInfo nodeCommitInfo);
        
        void FinalizeBlock(long blockNumber, TrieNode? root);

        void UpdateRefs(TrieNode trieNode, int refChange);
    }
}