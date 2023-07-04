// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Logging;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), (typeof(InitializeNetwork)))]
    public class ReviewBlockTree : IStep
    {
        private readonly IApiWithBlockchain _api;
        private ILogger _logger;

        public ReviewBlockTree(INethermindApi api)
        {
            _api = api;
            _logger = _api.LogManager.GetClassLogger();
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.DbProvider));

            if (_api.Config<IInitConfig>().ProcessingEnabled)
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
            ISyncConfig syncConfig = _api.Config<ISyncConfig>();
            if (!syncConfig.SynchronizationEnabled)
            {
                return;
            }

            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));

            if (!syncConfig.FastSync)
            {
                DbBlocksLoader loader = new(_api.BlockTree, _logger);
                await _api.BlockTree.Accept(loader, cancellationToken).ContinueWith(t =>
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
                StartupBlockTreeFixer fixer = new(syncConfig, _api.BlockTree, _api.DbProvider!.StateDb, _logger!);
                await _api.BlockTree.Accept(fixer, cancellationToken).ContinueWith(t =>
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
