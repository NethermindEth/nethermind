// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.PartialArchive;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Utils;
using Nethermind.Config;
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
    IPruningConfig pruningConfig,
    IDbProvider dbProvider,
    IBlockTree blockTree,
    MainPruningTrieStoreFactory mainPruningTrieStoreFactory,
    INodeStorage mainNodeStorage,
    IProcessExitSource processExit,
    IDisposableStack disposeStack,
    IFullPrunerFactory fullPrunerFactory,
    CompositePruningTrigger compositePruningTrigger,
    Lazy<IPathRecovery> pathRecovery,
    ILogManager logManager,
    NodeStorageCache? nodeStorageCache = null,
    PartialArchiveNodeTracker? partialArchiveTracker = null
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
            pruningConfig,
            new LastNStateRootTracker(blockTree, syncConfig.SnapServingMaxDepth),
            retentionWindowBlocksOverride: syncConfig.PartialArchiveEnabled ? syncConfig.PartialArchiveRange : null);

        if (partialArchiveTracker is not null)
        {
            // Pushed before the trie store so it is disposed after it (LIFO): the trie store's
            // shutdown persistence still reports events through the tracker.
            disposeStack.Push(partialArchiveTracker);
        }

        disposeStack.Push(mainWorldTrieStore);

        if (partialArchiveTracker is not null)
        {
            PartialArchivePruneTrigger pruneTrigger = new(
                partialArchiveTracker,
                blockTree,
                syncConfig,
                stateManager as IStateBoundaryWriter,
                logManager);
            disposeStack.Push(pruneTrigger);
            if (_logger.IsInfo) _logger.Info($"Partial archive mode enabled: retaining historical state for at least {syncConfig.PartialArchiveRange} blocks, pruning every {syncConfig.PartialArchivePruneInterval} blocks.");
        }

        FullPruner? fullPruner = fullPrunerFactory.Create(stateManager, trieStore);
        if (fullPruner is not null)
        {
            disposeStack.Push(fullPruner);
        }

        VerifyTrieStarter verifyTrieStarter = new(stateManager, processExit!, logManager);
        ManualPruningTrigger pruningTrigger = new();
        compositePruningTrigger.Add(pruningTrigger);
        disposeStack.Push(compositePruningTrigger);
        PruningTrieStateAdminRpcModule adminRpcModule = new(
            pruningTrigger,
            blockTree,
            stateManager.GlobalStateReader,
            verifyTrieStarter!
        );

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
        ILogManager logManager,
        IPersistedNodeObserver? persistedNodeObserver = null
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
        bool partialArchive = syncConfig.PartialArchiveEnabled;

        IPersistenceStrategy persistenceStrategy;
        if (partialArchive)
        {
            // The rolling historical window requires every block's state on disk; the window
            // pruner deletes superseded keys once they leave the window.
            persistenceStrategy = Persist.EveryNBlock(1);
        }
        else if (pruningConfig.Mode.IsMemory())
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

        // Partial archive defers obsolete-key deletion to its window pruner, so the in-memory
        // prune must not delete superseded keys eagerly.
        if (!pruningConfig.Mode.IsMemory() || partialArchive)
        {
            pruningStrategy = pruningStrategy
                .DontDeleteObsoleteNode();
        }

        if (stateDb is IFullPruningDb fullPruningDb)
        {
            pruningStrategy = new PruningTriggerPruningStrategy(fullPruningDb, pruningStrategy);
        }

        INodeStorage mainNodeStorage = nodeStorageFactory.WrapKeyValueStore(stateDb);

        if (partialArchive)
        {
            ValidatePartialArchiveConfig(syncConfig, pruningConfig, mainNodeStorage);
        }

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
            partialArchive ? persistedNodeObserver : null);
    }

    private void ValidatePartialArchiveConfig(ISyncConfig syncConfig, IPruningConfig pruningConfig, INodeStorage nodeStorage)
    {
        if (nodeStorage.Scheme is not INodeStorage.KeyScheme.HalfPath)
        {
            // Hash-keyed nodes are deduplicated across the whole trie, so a superseded key may
            // still be referenced by another live subtree and cannot be safely deleted.
            throw new InvalidConfigurationException(
                $"{nameof(ISyncConfig.PartialArchiveEnabled)} requires the HalfPath state layout, but the state database uses the {nodeStorage.Scheme} scheme.", -1);
        }

        if (pruningConfig.FullPruningTrigger is not FullPruningTrigger.Manual)
        {
            throw new InvalidConfigurationException(
                $"{nameof(ISyncConfig.PartialArchiveEnabled)} is incompatible with automatic full pruning (full pruning discards the historical window). Set {nameof(IPruningConfig)}.{nameof(IPruningConfig.FullPruningTrigger)} to {nameof(FullPruningTrigger.Manual)}.", -1);
        }

        if (syncConfig.PartialArchiveRange < pruningConfig.PruningBoundary)
        {
            if (_logger.IsWarn) _logger.Warn($"{nameof(ISyncConfig.PartialArchiveRange)} ({syncConfig.PartialArchiveRange}) is smaller than the pruning boundary ({pruningConfig.PruningBoundary}); raising it to the boundary.");
            syncConfig.PartialArchiveRange = pruningConfig.PruningBoundary;
        }
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
