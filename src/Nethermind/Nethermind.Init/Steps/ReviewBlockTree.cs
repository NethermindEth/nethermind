// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(LoadGenesisBlock))]
    public class ReviewBlockTree(
        IWorldStateManager worldStateManager,
        IInitConfig initConfig,
        ISyncConfig syncConfig,
        IBlockTree blockTree,
        ILogManager logManager
    ) : IStep
    {
        private readonly ILogger _logger = logManager.GetClassLogger();

        public Task Execute(CancellationToken cancellationToken)
        {
            if (initConfig.ProcessingEnabled)
            {
                return RunBlockTreeInitTasks(cancellationToken);
            }
            else
            {
                return Task.CompletedTask;
            }
        }

        private async Task RunBlockTreeInitTasks(CancellationToken cancellationToken)
        {
            if (!syncConfig.FastSync)
            {
                using DbBlocksLoader loader = new(blockTree, _logger);
                await blockTree.Accept(loader, cancellationToken).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error("Loading blocks from the DB failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (_logger.IsWarn) _logger.Warn("Loading blocks from the DB canceled.");
                    }
                });
            }
            else
            {
                using StartupBlockTreeFixer fixer = new(syncConfig, blockTree, worldStateManager!.GlobalStateReader, _logger!);
                await blockTree.Accept(fixer, cancellationToken).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error("Fixing gaps in DB failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (_logger.IsWarn) _logger.Warn("Fixing gaps in DB canceled.");
                    }
                });
            }
        }
    }
}
