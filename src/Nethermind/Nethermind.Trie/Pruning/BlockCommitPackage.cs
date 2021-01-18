//  Copyright (c) 2021 Demerzel Solutions Limited
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

namespace Nethermind.Trie.Pruning
{
    internal class BlockCommitSet
    {
        public long BlockNumber { get; }
        
        public TrieNode? Root { get; set; }

        public bool IsSealed { get; private set; }

        public long MemorySizeOfCommittedNodes { get; set; }

        public BlockCommitSet(long blockNumber)
        {
            BlockNumber = blockNumber;
        }

        public void Seal()
        {
            IsSealed = true;
        }

        public override string ToString()
        {
            return $"{BlockNumber}({Root})";
        }
    }
}
