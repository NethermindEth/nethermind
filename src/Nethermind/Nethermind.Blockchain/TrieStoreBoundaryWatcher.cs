// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain
{
    /// <summary>
    /// Watches state persistence in <see cref="ITrieStore"/> with <see cref="ITrieStore.ReorgBoundaryReached"/> and saves it in <see cref="IBlockFinder.BestPersistedState"/>.
    /// </summary>
    public class TrieStoreBoundaryWatcher : IDisposable
    {
        private readonly IStoreWithReorgBoundary _trieStore;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        public TrieStoreBoundaryWatcher(IStoreWithReorgBoundary trieStore, IBlockTree blockTree, ILogManager logManager)
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
