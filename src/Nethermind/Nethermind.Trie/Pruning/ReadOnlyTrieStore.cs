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
// 

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class ReadOnlyTrieStore : ITrieStore
    {
        private readonly ITrieNodeResolver _trieStore;

        public ReadOnlyTrieStore(ITrieNodeResolver trieStore)
        {
            _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        }

        public TrieNode FindCachedOrUnknown(Keccak hash)
        {
            return _trieStore.FindCachedOrUnknown(hash);
        }

        public byte[] LoadRlp(Keccak hash, bool allowCaching)
        {
            return _trieStore.LoadRlp(hash, allowCaching);
        }

        public void CommitOneNode(long blockNumber, NodeCommitInfo nodeCommitInfo)
        {
        }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root)
        {
        }

        public void UndoOneBlock()
        {
        }

        public event EventHandler<BlockNumberEventArgs> SnapshotTaken
        {
            add { }
            remove { }
        }
    }
}