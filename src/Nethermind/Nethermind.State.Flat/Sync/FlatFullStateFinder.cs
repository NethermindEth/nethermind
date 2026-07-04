// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.State.Flat.Sync;

public class FlatFullStateFinder(IPersistenceManager persistenceManager, IBlockTree blockTree, ILogManager logManager) : IFullStateFinder
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatFullStateFinder>();

    public ulong FindBestFullState()
    {
        StateId stateId = persistenceManager.GetCurrentPersistedStateId();
        if (stateId == StateId.PreGenesis) return 0UL;

        ulong bestHeader = blockTree.BestSuggestedHeader?.Number ?? 0;
        if (stateId.BlockNumber <= bestHeader) return stateId.BlockNumber;
        if (_logger.IsDebug) _logger.Debug($"Clamping best full state {stateId.BlockNumber} to best suggested header {bestHeader}");
        return bestHeader;

    }
}
