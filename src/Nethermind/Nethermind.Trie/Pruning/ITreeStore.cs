using System;

namespace Nethermind.Trie.Pruning
{
    public interface ITreeStore : ITrieNodeResolver
    {
        void Commit(long blockNumber, NodeCommitInfo nodeCommitInfo);

        // TODO: collect all storage roots, treat them separately
        void FinishBlockCommit(long blockNumber, TrieNode? root);

        public void Unwind();

        public event EventHandler<BlockNumberEventArgs> Stored;
    }
}
