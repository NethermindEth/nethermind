//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    internal class BlockCommitPackage
    {
        public TrieNode? Root { get; set; }
        
        /// <summary>
        /// Cannot hold Keccaks only here as we need the physical references to prevent them from being resolved
        /// from fresh.
        /// </summary>
        public List<TrieNode> StorageRoots { get; set; } = new List<TrieNode>();
        
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

        public void LogContent(ILogger logger)
        {
            if (logger.IsTrace)
            {
                foreach (TrieNode trieNode in _queue)
                {
                    logger.Trace($"  Queued {trieNode}");    
                }
            }
        }
    }
}