// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Trie.Pruning;

namespace Nethermind.Facade.Simulate;

public class SimulateReadOnlyBlocksProcessingEnvFactory(
    IWorldStateManager worldStateManager,
    IReadOnlyBlockTree baseBlockTree,
    IDbProvider dbProvider,
    ISpecProvider specProvider,
    ILogManager? logManager = null)
{
    public SimulateReadOnlyBlocksProcessingEnv Create(bool traceTransfers, bool validate)
    {
        IReadOnlyDbProvider editableDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        OverlayTrieStore overlayTrieStore = new(editableDbProvider.StateDb, worldStateManager.TrieStore, logManager);
        OverlayWorldStateManager overlayWorldStateManager = new(editableDbProvider, overlayTrieStore, logManager);
        BlockTree tempBlockTree = CreateTempBlockTree(editableDbProvider, specProvider, logManager, editableDbProvider);

        return new SimulateReadOnlyBlocksProcessingEnv(
            traceTransfers,
            overlayWorldStateManager,
            baseBlockTree,
            editableDbProvider,
            tempBlockTree,
            specProvider,
            logManager,
            validate);
    }

    private static BlockTree CreateTempBlockTree(IReadOnlyDbProvider readOnlyDbProvider, ISpecProvider? specProvider, ILogManager? logManager, IReadOnlyDbProvider editableDbProvider)
    {
        IBlockStore blockStore = new BlockStore(editableDbProvider.BlocksDb);
        IHeaderStore headerStore = new HeaderStore(editableDbProvider.HeadersDb, editableDbProvider.BlockNumbersDb);
        const int badBlocksStored = 1;
        IBlockStore badBlockStore = new BlockStore(editableDbProvider.BadBlocksDb, badBlocksStored);

        return new(blockStore,
            headerStore,
            editableDbProvider.BlockInfosDb,
            editableDbProvider.MetadataDb,
            badBlockStore,
            new ChainLevelInfoRepository(readOnlyDbProvider.BlockInfosDb),
            specProvider,
            NullBloomStorage.Instance,
            new SyncConfig(),
            logManager);
    }
}
