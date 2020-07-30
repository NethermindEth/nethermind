using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    internal class BlockCommitPackage
    {
        public TrieNode? Root { get; set; }
        
        public long BlockNumber { get; }

        public bool IsSealed { get; set; }

        public long MemorySize { get; private set; } =
            MemorySizes.Align(
                MemorySizes.SmallObjectOverhead +
                sizeof(long) +
                sizeof(bool) +
                MemorySizes.RefSize -
                -MemorySizes.SmallObjectFreeDataSize) +
                32 /* queue */ +
                0 /* queue array - not counted */;

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
            MemorySize += trieNode.GetMemorySize(false);
        }

        public bool TryDequeue(out TrieNode trieNode)
        {
            if (!IsSealed)
            {
                throw new InvalidOperationException("Trying to dequeue from an unsealed commit package.");
            }

            bool success = _queue.TryDequeue(out trieNode);
            if (success)
            {
                MemorySize -= trieNode.GetMemorySize(false);
            }

            return success;
        }

        public override string ToString()
        {
            return $"{BlockNumber}({_queue.Count})";
        }

        #region private

        /// <summary>
        /// TODO: the actual queue is not accounted for in memory calculations
        /// </summary>
        private Queue<TrieNode> _queue = new Queue<TrieNode>();

        #endregion
    }
}