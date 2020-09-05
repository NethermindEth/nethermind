//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Api;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor))]
    public class ReviewBlockTree : IStep
    {
        private readonly NethermindApi _api;
        private ILogger _logger;

        public ReviewBlockTree(NethermindApi api)
        {
            _api = api;
            _logger = _api.LogManager.GetClassLogger();
        }

        public Task Execute(CancellationToken cancellationToken)
        {
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
            
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));

            if (!syncConfig.FastSync && !syncConfig.BeamSync)
            {
                DbBlocksLoader loader = new DbBlocksLoader(_api.BlockTree, _logger);
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
                StartupBlockTreeFixer fixer = new StartupBlockTreeFixer(_api.Config<ISyncConfig>(), _api.BlockTree, _logger);
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