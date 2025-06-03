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
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Init;

[RunnerStepDependencies(typeof(InitializeBlockTree))]
[RunnerStepDependents(typeof(InitializeBlockchain))]
public class RunVerifyTrie : IStep
{
    private readonly IBlockTree _blockTree;
    private readonly IWorldStateManager _worldStateManager;
    private readonly IInitConfig _initConfig;
    private readonly IProcessExitSource _processExit;
    private ILogger _logger;

    public RunVerifyTrie(
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

        _logger = logManager.GetClassLogger<RunVerifyTrie>();
    }

    public Task Execute(CancellationToken cancellationToken)
    {
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
}
