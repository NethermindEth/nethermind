namespace Nethermind.Trie.Pruning
{
    public interface ITreeStore : ITrieNodeResolver
    {
        void Commit(long blockNumber, NodeCommitInfo nodeCommitInfo);

        void FinishBlockCommit(long blockNumber, TrieNode? root);

        public void Unwind();
    }
}
