// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain
{
    /// <summary>
    /// Watches state persistence in <see cref="IWorldStateManager"/> with <see cref="IWorldStateManager.ReorgBoundaryReached"/> and saves it in <see cref="IBlockFinder.BestPersistedState"/>.
    /// </summary>
    // No IDisposable: the subscription is safe to leave open because both this watcher and IWorldStateManager
    // share the same container lifetime. Explicit unsubscription would fire before the trie store disposes,
    // causing the final PersistOnShutdown ReorgBoundaryReached event to be missed.
    public class PersistedStateWatcher
    {
        private readonly IWorldStateManager _worldStateManager;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        public PersistedStateWatcher(IWorldStateManager worldStateManager, IBlockTree blockTree, ILogManager logManager)
        {
            _worldStateManager = worldStateManager;
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger<PersistedStateWatcher>();
            _worldStateManager.ReorgBoundaryReached += OnReorgBoundaryReached;
        }

        private void OnReorgBoundaryReached(object? sender, ReorgBoundaryReached e)
        {
            if (_logger.IsDebug) _logger.Debug($"Saving reorg boundary {e.BlockNumber}");
            _blockTree.BestPersistedState = e.BlockNumber;
        }
    }
}
