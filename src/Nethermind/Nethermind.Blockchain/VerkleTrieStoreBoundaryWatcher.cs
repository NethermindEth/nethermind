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
// 

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.Trie;

namespace Nethermind.Blockchain
{
    /// <summary>
    /// Watches state persistence in <see cref="ITrieStore"/> with <see cref="ITrieStore.ReorgBoundaryReached"/> and saves it in <see cref="IBlockFinder.BestPersistedState"/>.
    /// </summary>
    public class VerkleTrieStoreBoundaryWatcher : IDisposable
    {
        private readonly IVerkleTrieStore _trieStore;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        public VerkleTrieStoreBoundaryWatcher(IVerkleTrieStore trieStore, IBlockTree blockTree, ILogManager logManager)
        {
            _trieStore = trieStore;
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger();
            _trieStore.ReorgBoundaryReached += OnReorgBoundaryReached;
        }

        private void OnReorgBoundaryReached(object? sender, ReorgBoundaryReached e)
        {
            if (_logger.IsDebug) _logger.Debug($"Saving reorg boundary {e.BlockNumber}");
            _blockTree.BestPersistedState = e.BlockNumber;
        }

        public void Dispose()
        {
            _trieStore.ReorgBoundaryReached -= OnReorgBoundaryReached;
        }
    }
}
