// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core.Crypto;
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
    private readonly IPruningConfig _pruningConfig;
    private readonly ITrieStore _trieStore;
    private readonly IFullPruningDb _fullPruningDb;
    private readonly IWorldStateManager _worldStateManager;
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;
    private readonly ILogManager _logManager;
    private readonly IInitConfig _initConfig;
    private readonly IDb _codeDb;
    private readonly IComponentContext _ctx;

    public InitializeStateDb(
        IComponentContext ctx,

        ITrieStore trieStore,
        IFullPruningDb fullPruningDb,
        IPruningConfig pruningConfig,
        IInitConfig initConfig,
        IBlockTree blockTree,
        IWorldStateManager worldStateManager,
        ILogger logger,
        ILogManager logManager,
        [KeyFilter(DbNames.Code)] IDb codeDb
    )
    {
        _ctx = ctx;
        _trieStore = trieStore;
        _fullPruningDb = fullPruningDb;
        _pruningConfig = pruningConfig;
        _initConfig = initConfig;
        _blockTree = blockTree;
        _worldStateManager = worldStateManager;
        _logger = logger;
        _logManager = logManager;
        _codeDb = codeDb;
    }

    private static void InitBlockTraceDumper()
    {
        EthereumJsonSerializer.AddConverter(new TxReceiptConverter());
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        InitBlockTraceDumper();

        _worldStateManager.GlobalWorldState.StateRoot = _blockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash;

        if (_initConfig.DiagnosticMode == DiagnosticMode.VerifyTrie)
        {
            Task.Run(RunVerifyTrie);
        }

        // Init state if we need system calls before actual processing starts
        if (_blockTree!.Head?.StateRoot is not null)
        {
            _worldStateManager.GlobalWorldState.StateRoot = _blockTree.Head.StateRoot;
        }

        if (_pruningConfig.Mode.IsFull())
        {
            _fullPruningDb.PruningStarted += (_, args) =>
            {
                // _trieStore.PersistCache(args.Context, args.Context.CancellationTokenSource.Token);
            };
            _ctx.Resolve<FullPruner>();
        }

        _ctx.Resolve<TrieStoreBoundaryWatcher>();
        return Task.CompletedTask;
    }

    private void RunVerifyTrie()
    {
        try
        {
            _logger!.Info("Collecting trie stats and verifying that no nodes are missing...");
            IStateReader diagStateProvider = _worldStateManager.GlobalStateReader;
            Hash256 stateRoot = _blockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash;
            TrieStats stats = diagStateProvider.CollectStats(stateRoot, _codeDb, _logManager);
            _logger.Info($"Starting from {_blockTree.Head?.Number} {_blockTree.Head?.StateRoot}{Environment.NewLine}" + stats);
        }
        catch (Exception ex)
        {
            _logger!.Error(ex.ToString());
        }
    }
}
