// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Converters;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.State.Witnesses;
using Nethermind.Synchronization.Trie;
using Nethermind.Synchronization.Witness;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Init;

[RunnerStepDependencies(typeof(InitializePlugins), typeof(InitializeBlockTree), typeof(SetupKeyStore))]
public class InitializeStateDb : IStep
{
    private readonly INethermindApi _api;
    private ILogger _logger;

    public InitializeStateDb(INethermindApi api)
    {
        _api = api;
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        InitBlockTraceDumper();

        (IApiWithStores getApi, IApiWithBlockchain setApi) = _api.ForBlockchain;

        if (getApi.ChainSpec is null) throw new StepDependencyException(nameof(getApi.ChainSpec));
        if (getApi.DbProvider is null) throw new StepDependencyException(nameof(getApi.DbProvider));
        if (getApi.SpecProvider is null) throw new StepDependencyException(nameof(getApi.SpecProvider));
        if (getApi.BlockTree is null) throw new StepDependencyException(nameof(getApi.BlockTree));

        _logger = getApi.LogManager.GetClassLogger();
        ISyncConfig syncConfig = getApi.Config<ISyncConfig>();
        IPruningConfig pruningConfig = getApi.Config<IPruningConfig>();
        IInitConfig initConfig = getApi.Config<IInitConfig>();

        if (syncConfig.DownloadReceiptsInFastSync && !syncConfig.DownloadBodiesInFastSync)
        {
            if (_logger.IsWarn) _logger.Warn($"{nameof(syncConfig.DownloadReceiptsInFastSync)} is selected but {nameof(syncConfig.DownloadBodiesInFastSync)} - enabling bodies to support receipts download.");
            syncConfig.DownloadBodiesInFastSync = true;
        }

        IWitnessCollector witnessCollector;
        if (syncConfig.WitnessProtocolEnabled)
        {
            WitnessCollector witnessCollectorImpl = new(getApi.DbProvider.WitnessDb, _api.LogManager);
            witnessCollector = setApi.WitnessCollector = witnessCollectorImpl;
            setApi.WitnessRepository = witnessCollectorImpl.WithPruning(getApi.BlockTree!, getApi.LogManager);
        }
        else
        {
            witnessCollector = setApi.WitnessCollector = NullWitnessCollector.Instance;
            setApi.WitnessRepository = NullWitnessCollector.Instance;
        }

        CachingStore cachedStateDb = getApi.DbProvider.StateDb
            .Cached(Trie.MemoryAllowance.TrieNodeCacheCount);
        IKeyValueStore codeDb = getApi.DbProvider.CodeDb
            .WitnessedBy(witnessCollector);

        IKeyValueStoreWithBatching stateWitnessedBy = cachedStateDb.WitnessedBy(witnessCollector);
        IPersistenceStrategy persistenceStrategy;
        IPruningStrategy pruningStrategy;
        if (pruningConfig.Mode.IsMemory())
        {
            persistenceStrategy = Persist.IfBlockOlderThan(pruningConfig.PersistenceInterval); // TODO: this should be based on time
            if (pruningConfig.Mode.IsFull())
            {
                PruningTriggerPersistenceStrategy triggerPersistenceStrategy = new((IFullPruningDb)getApi.DbProvider!.StateDb, getApi.BlockTree!, getApi.LogManager);
                getApi.DisposeStack.Push(triggerPersistenceStrategy);
                persistenceStrategy = persistenceStrategy.Or(triggerPersistenceStrategy);
            }

            pruningStrategy = Prune.WhenCacheReaches(pruningConfig.CacheMb.MB()); // TODO: memory hint should define this
        }
        else
        {
            pruningStrategy = No.Pruning;
            persistenceStrategy = Persist.EveryBlock;
        }

        TrieStore trieStore = syncConfig.TrieHealing
            ? new HealingTrieStore(
                stateWitnessedBy,
                pruningStrategy,
                persistenceStrategy,
                getApi.LogManager)
            : new TrieStore(
                stateWitnessedBy,
                pruningStrategy,
                persistenceStrategy,
                getApi.LogManager);

        // TODO: Needed by node serving. Probably should use `StateReader` instead.
        setApi.TrieStore = trieStore;

        IWorldState worldState = syncConfig.TrieHealing
            ? new HealingWorldState(
                trieStore,
                codeDb,
                getApi.LogManager)
            : new WorldState(
                trieStore,
                codeDb,
                getApi.LogManager);

        if (pruningConfig.Mode.IsFull())
        {
            IFullPruningDb fullPruningDb = (IFullPruningDb)getApi.DbProvider!.StateDb;
            fullPruningDb.PruningStarted += (_, args) =>
            {
                cachedStateDb.PersistCache(args.Context);
                trieStore.PersistCache(args.Context, args.Context.CancellationTokenSource.Token);
            };
        }

        // This is probably the point where a different state implementation would switch.
        IWorldStateManager stateManager = setApi.WorldStateManager = new WorldStateManager(
            worldState,
            trieStore,
            getApi.DbProvider,
            getApi.LogManager);

        // TODO: Don't forget this
        TrieStoreBoundaryWatcher trieStoreBoundaryWatcher = new(stateManager, _api.BlockTree!, _api.LogManager);
        getApi.DisposeStack.Push(trieStoreBoundaryWatcher);
        getApi.DisposeStack.Push(trieStore);

        setApi.WorldState = stateManager.GlobalWorldState;
        setApi.StateReader = stateManager.GlobalStateReader;
        setApi.ChainHeadStateProvider = new ChainHeadReadOnlyStateProvider(getApi.BlockTree, stateManager.GlobalStateReader);

        worldState.StateRoot = getApi.BlockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash;

        if (_api.Config<IInitConfig>().DiagnosticMode == DiagnosticMode.VerifyTrie)
        {
            Task.Run(() =>
            {
                try
                {
                    _logger!.Info("Collecting trie stats and verifying that no nodes are missing...");
                    IWorldState diagStateProvider = stateManager.GlobalWorldState;
                    diagStateProvider.StateRoot = getApi.BlockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash;
                    TrieStats stats = diagStateProvider.CollectStats(getApi.DbProvider.CodeDb, _api.LogManager);
                    _logger.Info($"Starting from {getApi.BlockTree.Head?.Number} {getApi.BlockTree.Head?.StateRoot}{Environment.NewLine}" + stats);
                }
                catch (Exception ex)
                {
                    _logger!.Error(ex.ToString());
                }
            });
        }

        // Init state if we need system calls before actual processing starts
        if (getApi.BlockTree!.Head?.StateRoot is not null)
        {
            worldState.StateRoot = getApi.BlockTree.Head.StateRoot;
        }

        InitializeFullPruning(pruningConfig, initConfig, _api, stateManager.GlobalStateReader);

        return Task.CompletedTask;
    }

    private static void InitBlockTraceDumper()
    {
        EthereumJsonSerializer.AddConverter(new TxReceiptConverter());
    }

    private static void InitializeFullPruning(
        IPruningConfig pruningConfig,
        IInitConfig initConfig,
        INethermindApi api,
        IStateReader stateReader)
    {
        IPruningTrigger? CreateAutomaticTrigger(string dbPath)
        {
            long threshold = pruningConfig.FullPruningThresholdMb.MB();

            switch (pruningConfig.FullPruningTrigger)
            {
                case FullPruningTrigger.StateDbSize:
                    return new PathSizePruningTrigger(dbPath, threshold, api.TimerFactory, api.FileSystem);
                case FullPruningTrigger.VolumeFreeSpace:
                    return new DiskFreeSpacePruningTrigger(dbPath, threshold, api.TimerFactory, api.FileSystem);
                default:
                    return null;
            }
        }

        if (pruningConfig.Mode.IsFull())
        {
            IDb stateDb = api.DbProvider!.StateDb;
            if (stateDb is IFullPruningDb fullPruningDb)
            {
                string pruningDbPath = fullPruningDb.GetPath(initConfig.BaseDbPath);
                IPruningTrigger? pruningTrigger = CreateAutomaticTrigger(pruningDbPath);
                if (pruningTrigger is not null)
                {
                    api.PruningTrigger.Add(pruningTrigger);
                }

                IDriveInfo? drive = api.FileSystem.GetDriveInfos(pruningDbPath).FirstOrDefault();
                FullPruner pruner = new(fullPruningDb, api.PruningTrigger, pruningConfig, api.BlockTree!,
                    stateReader, api.ProcessExit!, ChainSizes.CreateChainSizeInfo(api.ChainSpec.ChainId),
                    drive, api.LogManager);
                api.DisposeStack.Push(pruner);
            }
        }
    }
}
