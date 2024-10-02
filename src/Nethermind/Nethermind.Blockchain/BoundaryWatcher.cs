// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain
{
    /// <summary>
    /// Watches state persistence in <see cref="IWorldStateManager"/> with <see cref="IWorldStateManager.ReorgBoundaryReached"/> and saves it in <see cref="IBlockFinder.BestPersistedState"/>.
    /// </summary>
    public class BoundaryWatcher : IDisposable
    {
        private readonly IWorldStateManager _manager;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        public BoundaryWatcher(IWorldStateManager manager, IBlockTree blockTree, ILogManager logManager)
        {
            _manager = manager;
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger();
            _manager.ReorgBoundaryReached += OnReorgBoundaryReached;
        }

        private void OnReorgBoundaryReached(object? sender, ReorgBoundaryReached e)
        {
            if (_logger.IsDebug) _logger.Debug($"Saving reorg boundary {e.BlockNumber}");
            _blockTree.BestPersistedState = e.BlockNumber;
        }

        public void Dispose()
        {
            _manager.ReorgBoundaryReached -= OnReorgBoundaryReached;
        }
    }
}
