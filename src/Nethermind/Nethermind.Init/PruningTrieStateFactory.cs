// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Linq;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Utils;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks.Config;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Healing;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Init;

public class PruningTrieStateFactory(
    ISyncConfig syncConfig,
    IInitConfig initConfig,
    IPruningConfig pruningConfig,
    IBlocksConfig blockConfig,
    IDbConfig dbConfig,
    IDbProvider dbProvider,
    IBlockTree blockTree,
    IFileSystem fileSystem,
    ITimerFactory timerFactory,
    MainPruningTrieStoreFactory mainPruningTrieStoreFactory,
    INodeStorageFactory nodeStorageFactory,
    INodeStorage mainNodeStorage,
    IProcessExitSource processExit,
    ChainSpec chainSpec,
    IDisposableStack disposeStack,
    ILogManager logManager
)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PruningTrieStateFactory>();

    public (IWorldStateManager, IPruningTrieStateAdminRpcModule) Build()
    {
        CompositePruningTrigger compositePruningTrigger = new CompositePruningTrigger();

        AdviseConfig();

        IPruningTrieStore trieStore = mainPruningTrieStoreFactory.PruningTrieStore;
        ITrieStore mainWorldTrieStore = trieStore;
        PreBlockCaches? preBlockCaches = null;
        if (blockConfig.PreWarmStateOnBlockProcessing)
        {
            preBlockCaches = new PreBlockCaches();
            mainWorldTrieStore = new PreCachedTrieStore(trieStore, preBlockCaches.RlpCache);
        }

        IKeyValueStoreWithBatching codeDb = dbProvider.CodeDb;
        IWorldState worldState = syncConfig.TrieHealing
            ? new HealingWorldState(
                mainWorldTrieStore,
                mainNodeStorage,
                codeDb,
                logManager,
                preBlockCaches,
                // Main thread should only read from prewarm caches, not spend extra time updating them.
                populatePreBlockCache: false)
            : new WorldState(
                mainWorldTrieStore,
                codeDb,
                logManager,
                preBlockCaches,
                // Main thread should only read from prewarm caches, not spend extra time updating them.
                populatePreBlockCache: false);

        // Init state if we need system calls before actual processing starts
        worldState.StateRoot = blockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash;
        if (blockTree!.Head?.StateRoot is not null)
        {
            worldState.StateRoot = blockTree.Head.StateRoot;
        }

        IWorldStateManager stateManager = new WorldStateManager(
            worldState,
            trieStore,
            dbProvider,
            logManager,
            new LastNStateRootTracker(blockTree, 128));

        // NOTE: Don't forget this! Very important!
        TrieStoreBoundaryWatcher trieStoreBoundaryWatcher = new(stateManager, blockTree!, logManager);
        // Must be disposed after main trie store or the final persist on dispose will not set persisted state on blocktree.
        disposeStack.Push(trieStoreBoundaryWatcher);

        disposeStack.Push(mainWorldTrieStore);

        InitializeFullPruning(
            dbProvider.StateDb,
            stateManager.GlobalStateReader,
            mainNodeStorage,
            nodeStorageFactory,
            trieStore,
            compositePruningTrigger,
            preBlockCaches
        );

        var verifyTrieStarter = new VerifyTrieStarter(stateManager, processExit!, logManager);
        ManualPruningTrigger pruningTrigger = new();
        compositePruningTrigger.Add(pruningTrigger);
        PruningTrieStateAdminRpcModule adminRpcModule = new PruningTrieStateAdminRpcModule(
            pruningTrigger,
            blockTree,
            stateManager.GlobalStateReader,
            verifyTrieStarter!
        );

        return (stateManager, adminRpcModule);
    }

    private void AdviseConfig()
    {
        // On a 7950x (32 logical coree), assuming write buffer is large enough, the pruning time is about 3 second
        // with 8GB of pruning cache. Lets assume that this is a safe estimate as the ssd can be a limitation also.
        long maximumCacheMb = Environment.ProcessorCount * 250;
        // It must be at least 1GB as on mainnet at least 500MB will remain to support snap sync. So pruning cache only drop to about 500MB after pruning.
        maximumCacheMb = Math.Max(1000, maximumCacheMb);
        if (pruningConfig.CacheMb > maximumCacheMb)
        {
            // The user can also change `--Db.StateDbWriteBufferSize`.
            // Which may or may not be better as each read will need to go through eacch write buffer.
            // So having less of them is probably better..
            if (_logger.IsWarn) _logger.Warn($"Detected {pruningConfig.CacheMb}MB of pruning cache config. Pruning cache more than {maximumCacheMb}MB is not recommended with {Environment.ProcessorCount} logical core as it may cause long memory pruning time which affect attestation.");
        }

        var totalWriteBufferMb = dbConfig.StateDbWriteBufferNumber * dbConfig.StateDbWriteBufferSize / (ulong)1.MB();
        var minimumWriteBufferMb = 0.2 * pruningConfig.CacheMb;
        if (totalWriteBufferMb < minimumWriteBufferMb)
        {
            long minimumWriteBufferSize = (int)Math.Ceiling((minimumWriteBufferMb * 1.MB()) / dbConfig.StateDbWriteBufferNumber);

            if (_logger.IsWarn) _logger.Warn($"Detected {totalWriteBufferMb}MB of maximum write buffer size. Write buffer size should be at least 20% of pruning cache MB or memory pruning may slow down. Try setting `--Db.{nameof(dbConfig.StateDbWriteBufferSize)} {minimumWriteBufferSize}`.");
        }

        if (pruningConfig.CacheMb <= pruningConfig.DirtyCacheMb)
        {
            throw new InvalidConfigurationException("Dirty pruning cache size must be less than persisted pruning cache size.", -1);
        }
    }

    private void InitializeFullPruning(IDb stateDb,
        IStateReader stateReader,
        INodeStorage mainNodeStorage,
        INodeStorageFactory nodeStorageFactory,
        IPruningTrieStore trieStore,
        CompositePruningTrigger compositePruningTrigger,
        PreBlockCaches? preBlockCaches)
    {
        IPruningTrigger? CreateAutomaticTrigger(string dbPath)
        {
            long threshold = pruningConfig.FullPruningThresholdMb.MB();

            switch (pruningConfig.FullPruningTrigger)
            {
                case FullPruningTrigger.StateDbSize:
                    if (_logger.IsInfo) _logger.Info($"Full pruning will activate when the database size reaches {threshold.SizeToString(true)} (={threshold.SizeToString()}).");
                    return new PathSizePruningTrigger(dbPath, threshold, timerFactory, fileSystem);
                case FullPruningTrigger.VolumeFreeSpace:
                    if (_logger.IsInfo) _logger.Info($"Full pruning will activate when disk free space drops below {threshold.SizeToString(true)} (={threshold.SizeToString()}).");
                    return new DiskFreeSpacePruningTrigger(dbPath, threshold, timerFactory, fileSystem);
                default:
                    return null;
            }
        }

        if (pruningConfig.Mode.IsFull() && stateDb is IFullPruningDb fullPruningDb)
        {
            string pruningDbPath = fullPruningDb.GetPath(initConfig.BaseDbPath);
            IPruningTrigger? pruningTrigger = CreateAutomaticTrigger(pruningDbPath);
            if (pruningTrigger is not null)
            {
                compositePruningTrigger.Add(pruningTrigger);
            }

            IDriveInfo? drive = fileSystem.GetDriveInfos(pruningDbPath).FirstOrDefault();
            FullPruner pruner = new(
                fullPruningDb,
                nodeStorageFactory,
                mainNodeStorage,
                compositePruningTrigger,
                pruningConfig,
                blockTree!,
                stateReader,
                processExit!,
                ChainSizes.CreateChainSizeInfo(chainSpec.ChainId),
                drive,
                trieStore,
                logManager);
            disposeStack.Push(pruner);
        }
    }
}

public class MainPruningTrieStoreFactory
{
    private readonly ILogger _logger;

    public MainPruningTrieStoreFactory(
        ISyncConfig syncConfig,
        IPruningConfig pruningConfig,
        IDbProvider dbProvider,
        INodeStorageFactory nodeStorageFactory,
        IBlockTree blockTree,
        IDisposableStack disposeStack,
        ILogManager logManager
        )
    {
        _logger = logManager.GetClassLogger<MainPruningTrieStoreFactory>();

        if (syncConfig.SnapServingEnabled == true && pruningConfig.PruningBoundary < 128)
        {
            if (_logger.IsInfo) _logger.Info($"Snap serving enabled, but {nameof(pruningConfig.PruningBoundary)} is less than 128. Setting to 128.");
            pruningConfig.PruningBoundary = 128;
        }

        if (pruningConfig.PruningBoundary < 64)
        {
            if (_logger.IsWarn) _logger.Warn($"Pruning boundary must be at least 64. Setting to 64.");
            pruningConfig.PruningBoundary = 64;
        }

        IDb stateDb = dbProvider.StateDb;
        IPersistenceStrategy persistenceStrategy;
        if (pruningConfig.Mode.IsMemory())
        {
            persistenceStrategy = No.Persistence;
        }
        else
        {
            persistenceStrategy = Persist.EveryNBlock(pruningConfig.PersistenceInterval);
        }

        IPruningStrategy pruningStrategy = Prune
            .WhenCacheReaches(pruningConfig.DirtyCacheMb.MB())
            .WhenPersistedCacheReaches(pruningConfig.CacheMb.MB() - pruningConfig.DirtyCacheMb.MB())
            .WhenLastPersistedBlockIsTooOld(pruningConfig.MaxUnpersistedBlockCount, pruningConfig.PruningBoundary)
            .UnlessLastPersistedBlockIsTooNew(pruningConfig.MinUnpersistedBlockCount, pruningConfig.PruningBoundary);

        if (!pruningConfig.Mode.IsMemory())
        {
            pruningStrategy = pruningStrategy
                .DontDeleteObsoleteNode();
        }

        if (stateDb is IFullPruningDb fullPruningDb)
        {
            // PruningTriggerPersistenceStrategy triggerPersistenceStrategy = new(fullPruningDb, logManager);
            // disposeStack.Push(triggerPersistenceStrategy);
            // persistenceStrategy = persistenceStrategy.Or(triggerPersistenceStrategy);
            pruningStrategy = new PruningTriggerPruningStrategy(fullPruningDb, pruningStrategy);
        }

        INodeStorage mainNodeStorage = nodeStorageFactory.WrapKeyValueStore(stateDb);

        PruningTrieStore = new TrieStore(
            mainNodeStorage,
            pruningStrategy,
            persistenceStrategy,
            pruningConfig,
            logManager);
    }

    public IPruningTrieStore PruningTrieStore { get; }
}
