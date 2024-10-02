// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Converters;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.Synchronization.Trie;
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
        IBlocksConfig blockConfig = getApi.Config<IBlocksConfig>();

        _api.NodeStorageFactory.DetectCurrentKeySchemeFrom(getApi.DbProvider.StateDb);

        syncConfig.SnapServingEnabled |= syncConfig.SnapServingEnabled is null
            && _api.NodeStorageFactory.CurrentKeyScheme is INodeStorage.KeyScheme.HalfPath or null
            && initConfig.StateDbKeyScheme != INodeStorage.KeyScheme.Hash;

        if (_api.NodeStorageFactory.CurrentKeyScheme is INodeStorage.KeyScheme.Hash
            || initConfig.StateDbKeyScheme == INodeStorage.KeyScheme.Hash)
        {
            // Special case in case its using hashdb, use a slightly different database configuration.
            if (_api.DbProvider?.StateDb is ITunableDb tunableDb) tunableDb.Tune(ITunableDb.TuneType.HashDb);
        }

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

        if (syncConfig.DownloadReceiptsInFastSync && !syncConfig.DownloadBodiesInFastSync)
        {
            if (_logger.IsWarn) _logger.Warn($"{nameof(syncConfig.DownloadReceiptsInFastSync)} is selected but {nameof(syncConfig.DownloadBodiesInFastSync)} - enabling bodies to support receipts download.");
            syncConfig.DownloadBodiesInFastSync = true;
        }

        IKeyValueStore codeDb = getApi.DbProvider.CodeDb;

        PreBlockCaches? preBlockCaches = null;
        if (blockConfig.PreWarmStateOnBlockProcessing)
        {
            preBlockCaches = new PreBlockCaches();
        }

        IStateFactory stateFactory = setApi.StateFactory!;

        // Main thread should only read from prewarm caches, not spend extra time updating them.
        IWorldState worldState = new WorldState(stateFactory, codeDb, getApi.LogManager, preBlockCaches,
            populatePreBlockCache: false);
        setApi.WorldState = worldState;

        // This is probably the point where a different state implementation would switch.
        IWorldStateManager stateManager = setApi.WorldStateManager = new WorldStateManager(
            worldState,
            stateFactory,
            getApi.DbProvider,
            getApi.LogManager);

        // TODO: Don't forget this
        BoundaryWatcher boundaryWatcher = new(stateManager, _api.BlockTree!, _api.LogManager);
        getApi.DisposeStack.Push(boundaryWatcher);

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
                    Hash256 stateRoot = getApi.BlockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash;
                    TrieStats stats = stateManager.GlobalStateReader.CollectStats(stateRoot, getApi.DbProvider.CodeDb, _api.LogManager);
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

        return Task.CompletedTask;
    }

    private static void InitBlockTraceDumper()
    {
        EthereumJsonSerializer.AddConverter(new TxReceiptConverter());
    }
}
