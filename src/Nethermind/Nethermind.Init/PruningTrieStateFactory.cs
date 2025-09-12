// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Linq;
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
using Nethermind.Evm.State;
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
    Lazy<IPathRecovery> pathRecovery,
    ILogManager logManager
)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PruningTrieStateFactory>();

    public (IWorldStateManager, IPruningTrieStateAdminRpcModule) Build()
    {
        CompositePruningTrigger compositePruningTrigger = new CompositePruningTrigger();

        IPruningTrieStore trieStore = mainPruningTrieStoreFactory.PruningTrieStore;
        ITrieStore mainWorldTrieStore = trieStore;
        PreBlockCaches? preBlockCaches = null;
        if (blockConfig.PreWarmStateOnBlockProcessing)
        {
            preBlockCaches = new PreBlockCaches();
            mainWorldTrieStore = new PreCachedTrieStore(trieStore, preBlockCaches.RlpCache);
        }

        IKeyValueStoreWithBatching codeDb = dbProvider.CodeDb;
        IWorldStateBackend backend = syncConfig.TrieHealing
            ? new HealingWorldStateBackend(
                mainWorldTrieStore,
                mainNodeStorage,
                pathRecovery,
                logManager)
            : new TrieStoreBackend(
                mainWorldTrieStore,
                logManager);

        IWorldState worldState = new WorldState(
                backend,
                codeDb,
                logManager,
                preBlockCaches,
                // Main thread should only read from prewarm caches, not spend extra time updating them.
                populatePreBlockCache: false);

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
        IDbConfig dbConfig,
        IHardwareInfo hardwareInfo,
        ILogManager logManager
    )
    {
        _logger = logManager.GetClassLogger<MainPruningTrieStoreFactory>();

        AdviseConfig(pruningConfig, dbConfig, hardwareInfo);

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

    private void AdviseConfig(IPruningConfig pruningConfig, IDbConfig dbConfig, IHardwareInfo hardwareInfo)
    {
        if (hardwareInfo.AvailableMemoryBytes >= IHardwareInfo.StateDbLargerMemoryThreshold)
        {
            // Default is 1280 MB, which translate to 280 MB of persisted cache memory (dirty node cache is 1000 MB).
            // So this actually increase it from 280 MB to 1000 MB, reducing dirty node load at DB by 50%.
            if (pruningConfig.CacheMb < 2000)
            {
                if (_logger.IsDebug) _logger.Debug($"Increasing pruning cache to 2 GB due to available additional memory.");
                pruningConfig.CacheMb = 2000;
            }
        }

        // On a 7950x (32 logical coree), assuming write buffer is large enough, the pruning time is about 3 second
        // with 8GB of pruning cache. Lets assume that this is a safe estimate as the ssd can be a limitation also.
        long maximumDirtyCacheMb = Environment.ProcessorCount * 250;
        // It must be at least 1GB as on mainnet at least 500MB will remain to support snap sync. So pruning cache only drop to about 500MB after pruning.
        maximumDirtyCacheMb = Math.Max(1000, maximumDirtyCacheMb);
        if (pruningConfig.DirtyCacheMb > maximumDirtyCacheMb)
        {
            // The user can also change `--Db.StateDbWriteBufferSize`.
            // Which may or may not be better as each read will need to go through eacch write buffer.
            // So having less of them is probably better..
            if (_logger.IsWarn) _logger.Warn($"Detected {pruningConfig.DirtyCacheMb}MB of dirty pruning cache config. Dirty cache more than {maximumDirtyCacheMb}MB is not recommended with {Environment.ProcessorCount} logical core as it may cause long memory pruning time which affect attestation.");
        }

        if (pruningConfig.CacheMb <= pruningConfig.DirtyCacheMb)
        {
            throw new InvalidConfigurationException("Dirty pruning cache size must be less than persisted pruning cache size.", -1);
        }
    }

    public IPruningTrieStore PruningTrieStore { get; }
}
