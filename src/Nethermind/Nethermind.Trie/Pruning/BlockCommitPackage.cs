using System;
using System.Collections.Generic;

namespace Nethermind.Trie.Pruning
{
    public class BlockCommitPackage
    {
        public long BlockNumber { get; }
        
        public bool IsSealed { get; set; }

        public BlockCommitPackage(long blockNumber)
        {
            BlockNumber = blockNumber;
        }
        
        public void Seal()
        {
            IsSealed = true;
        }

        public void Enqueue(TrieNode trieNode)
        {
            if (IsSealed)
            {
                throw new InvalidOperationException("Cannot add to a sealed commit package.");
            }
            
            _queue.Enqueue(trieNode);
        }
        
        public bool TryDequeue(out TrieNode trieNode)
        {
            if (!IsSealed)
            {
                throw new InvalidOperationException("Trying to dequeue from an unsealed commit package.");
            }

            return _queue.TryDequeue(out trieNode);
        }

        public override string ToString()
        {
            return $"{BlockNumber}({_queue.Count})";
        }

        #region private

        private Queue<TrieNode> _queue = new Queue<TrieNode>();

        #endregion
    }
}