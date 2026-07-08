// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Utils;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Db.LogIndex;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Healing;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Init;

public class PruningTrieStateFactory(
    ISyncConfig syncConfig,
    IDbProvider dbProvider,
    IBlockTree blockTree,
    MainPruningTrieStoreFactory mainPruningTrieStoreFactory,
    INodeStorage mainNodeStorage,
    IDisposableStack disposeStack,
    IFullPrunerFactory fullPrunerFactory,
    CompositePruningTrigger compositePruningTrigger,
    Lazy<IPathRecovery> pathRecovery,
    StateBoundaryStore boundaryStore,
    ILogManager logManager,
    NodeStorageCache? nodeStorageCache = null
)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PruningTrieStateFactory>();

    public (IWorldStateManager, IPruningTrieStateAdminRpcModule) Build()
    {
        IPruningTrieStore trieStore = mainPruningTrieStoreFactory.PruningTrieStore;

        ITrieStore mainWorldTrieStore = trieStore;

        if (nodeStorageCache is not null)
        {
            mainWorldTrieStore = new PreCachedTrieStore(mainWorldTrieStore, nodeStorageCache);
        }

        IKeyValueStoreWithBatching codeDb = dbProvider.CodeDb;
        IWorldStateScopeProvider scopeProvider = syncConfig.TrieHealing
            ? new HealingWorldStateScopeProvider(
                mainWorldTrieStore,
                codeDb,
                mainNodeStorage,
                pathRecovery,
                logManager)
            : new TrieStoreScopeProvider(
                mainWorldTrieStore,
                codeDb,
                logManager,
                codeDbIsPersistent: true);

        IWorldStateManager stateManager = new WorldStateManager(
            scopeProvider,
            trieStore,
            dbProvider,
            logManager,
            boundaryStore,
            new LastNStateRootTracker(blockTree, syncConfig.SnapServingMaxDepth));

        disposeStack.Push(mainWorldTrieStore);

        FullPruner? fullPruner = fullPrunerFactory.Create(stateManager, trieStore);
        if (fullPruner is not null)
        {
            disposeStack.Push(fullPruner);
        }

        ManualPruningTrigger pruningTrigger = new();
        compositePruningTrigger.Add(pruningTrigger);
        disposeStack.Push(compositePruningTrigger);
        PruningTrieStateAdminRpcModule adminRpcModule = new(pruningTrigger);

        return (stateManager, adminRpcModule);
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
        IFinalizedStateProvider finalizedStateProvider,
        IBlockTree blockTree,
        IDbConfig dbConfig,
        ILogIndexConfig logIndexConfig,
        IHardwareInfo hardwareInfo,
        IStatePersistenceBarrier persistenceBarrier,
        ILogManager logManager
    )
    {
        _logger = logManager.GetClassLogger<MainPruningTrieStoreFactory>();

        AdviseConfig(pruningConfig, dbConfig, hardwareInfo);

        if (syncConfig.SnapServingEnabled == true && pruningConfig.PruningBoundary < syncConfig.SnapServingMaxDepth)
        {
            logIndexConfig.MaxReorgDepth ??= pruningConfig.PruningBoundary;

            if (_logger.IsInfo) _logger.Info($"Snap serving enabled, but {nameof(pruningConfig.PruningBoundary)} is less than {syncConfig.SnapServingMaxDepth}. Setting to {syncConfig.SnapServingMaxDepth}.");
            pruningConfig.PruningBoundary = syncConfig.SnapServingMaxDepth;
        }

        if (pruningConfig.PruningBoundary < 64UL)
        {
            if (_logger.IsWarn) _logger.Warn($"Pruning boundary must be at least 64. Setting to 64.");
            pruningConfig.PruningBoundary = 64UL;
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
            .WhenCacheReaches(pruningConfig.DirtyCacheMb.MB)
            .WhenPersistedCacheReaches(pruningConfig.CacheMb.MB - pruningConfig.DirtyCacheMb.MB)
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

        if (pruningConfig.SimulateLongFinalizationDepth != 0UL)
        {
            finalizedStateProvider = new DelayedFinalizedStateProvider(finalizedStateProvider, blockTree, pruningConfig.SimulateLongFinalizationDepth);
        }

        PruningTrieStore = new TrieStore(
            mainNodeStorage,
            pruningStrategy,
            persistenceStrategy,
            finalizedStateProvider,
            pruningConfig,
            logManager,
            persistenceBarrier);
    }

    private void AdviseConfig(IPruningConfig pruningConfig, IDbConfig dbConfig, IHardwareInfo hardwareInfo)
    {
        if (hardwareInfo.AvailableMemoryBytes >= IHardwareInfo.StateDbLargerMemoryThreshold)
        {
            if (pruningConfig.CacheMb < 2000)
            {
                if (_logger.IsDebug) _logger.Debug($"Increasing pruning cache to 2 GB due to available additional memory.");
                pruningConfig.CacheMb = 2000;
            }
        }

        long maximumDirtyCacheMb = Environment.ProcessorCount * 250;
        maximumDirtyCacheMb = Math.Max(1000, maximumDirtyCacheMb);
        if (pruningConfig.DirtyCacheMb > maximumDirtyCacheMb)
        {
            if (_logger.IsWarn) _logger.Warn($"Detected {pruningConfig.DirtyCacheMb}MB of dirty pruning cache config. Dirty cache more than {maximumDirtyCacheMb}MB is not recommended with {Environment.ProcessorCount} logical core as it may cause long memory pruning time which affect attestation.");
        }

        if (pruningConfig.CacheMb <= pruningConfig.DirtyCacheMb)
        {
            throw new InvalidConfigurationException("Dirty pruning cache size must be less than persisted pruning cache size.", -1);
        }
    }

    public IPruningTrieStore PruningTrieStore { get; }

    private class DelayedFinalizedStateProvider(
        IFinalizedStateProvider finalizedStateProvider,
        IBlockTree blockTree,
        ulong pruningConfigSimulateLongFinalizationDepth
    ) : IFinalizedStateProvider
    {
        private ulong? _lastFinalizedBlockNumber = null;

        public ulong FinalizedBlockNumber
        {
            get
            {
                ulong baseFinalizedBlockNumber = finalizedStateProvider.FinalizedBlockNumber;

                ulong headNumber = blockTree.Head?.Number ?? 0UL;
                baseFinalizedBlockNumber = Math.Min(baseFinalizedBlockNumber, headNumber + pruningConfigSimulateLongFinalizationDepth / 2);

                if (_lastFinalizedBlockNumber is null || baseFinalizedBlockNumber - _lastFinalizedBlockNumber.Value > pruningConfigSimulateLongFinalizationDepth)
                {
                    _lastFinalizedBlockNumber = baseFinalizedBlockNumber;
                }

                return _lastFinalizedBlockNumber.Value;
            }
        }

        public Hash256? GetFinalizedStateRootAt(ulong blockNumber) => finalizedStateProvider.GetFinalizedStateRootAt(blockNumber);
    }
}
