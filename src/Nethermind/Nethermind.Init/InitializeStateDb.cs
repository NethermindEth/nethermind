// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Converters;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;

namespace Nethermind.Init;

[RunnerStepDependencies(typeof(InitializePlugins), typeof(InitializeBlockTree), typeof(SetupKeyStore))]
public class InitializeStateDb : IStep
{
    private readonly IBlockTree _blockTree;
    private readonly IWorldStateManager _worldStateManager;
    private readonly IInitConfig _initConfig;
    private readonly IProcessExitSource _processExit;
    private ILogger _logger;

    public InitializeStateDb(
        IWorldStateManager worldStateManager,
        IBlockTree blockTree,
        IInitConfig initConfig,
        IProcessExitSource processExitSource,
        ILogManager logManager)
    {
        _worldStateManager = worldStateManager;
        _blockTree = blockTree;
        _initConfig = initConfig;
        _processExit = processExitSource;

        _logger = logManager.GetClassLogger<InitializeStateDb>();
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        InitBlockTraceDumper();

        if (_initConfig.DiagnosticMode == DiagnosticMode.VerifyTrie)
        {
            _logger!.Info("Collecting trie stats and verifying that no nodes are missing...");
            BlockHeader? head = _blockTree!.Head?.Header;
            if (head is not null)
            {
                _logger.Info($"Starting from {head.Number} {head.StateRoot}{Environment.NewLine}");
                _worldStateManager.VerifyTrie(head, _processExit!.Token);
            }
        }

        return Task.CompletedTask;
    }

    private static void InitBlockTraceDumper()
    {
        // TODO: What is this? Why is this here? What does it have anything to do with state? Why is it doing something global?
        EthereumJsonSerializer.AddConverter(new TxReceiptConverter());
    }
}
