// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Converters;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.State.Healing;
using Nethermind.Synchronization.FastSync;
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
        IDbConfig dbConfig = getApi.Config<IDbConfig>();

        // This is probably the point where a different state implementation would switch.
        (IWorldStateManager stateManager, INodeStorage mainNodeStorage, CompositePruningTrigger pruningTrigger) = new PruningTrieStateFactory(
            syncConfig,
            initConfig,
            pruningConfig,
            blockConfig,
            dbConfig,
            _api.DbProvider!,
            _api.BlockTree!,
            _api.FileSystem,
            _api.TimerFactory,
            _api.ProcessExit!,
            _api.ChainSpec,
            _api.DisposeStack,
            _api.LogManager
        ).Build();

        setApi.WorldStateManager = stateManager;

        // Used by state sync.
        setApi.MainNodeStorage = mainNodeStorage;

        // Used by rpc to trigger pruning.
        setApi.PruningTrigger = pruningTrigger;

        setApi.StateReader = stateManager.GlobalStateReader;
        setApi.ChainHeadStateProvider = new ChainHeadReadOnlyStateProvider(getApi.BlockTree, stateManager.GlobalStateReader);
        setApi.VerifyTrieStarter = new VerifyTrieStarter(stateManager, _api.ProcessExit!, _api.LogManager);

        if (_api.Config<IInitConfig>().DiagnosticMode == DiagnosticMode.VerifyTrie)
        {
            _logger!.Info("Collecting trie stats and verifying that no nodes are missing...");
            BlockHeader? head = getApi.BlockTree!.Head?.Header;
            if (head is not null)
            {
                _logger.Info($"Starting from {head.Number} {head.StateRoot}{Environment.NewLine}");
                stateManager.VerifyTrie(head, setApi.ProcessExit!.Token);
            }
        }

        return Task.CompletedTask;
    }

    private static void InitBlockTraceDumper()
    {
        EthereumJsonSerializer.AddConverter(new TxReceiptConverter());
    }
}
