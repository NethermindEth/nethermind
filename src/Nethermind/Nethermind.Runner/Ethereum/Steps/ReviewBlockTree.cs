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
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor))]
    public class ReviewBlockTree : IStep
    {
        private readonly EthereumRunnerContext _context;
        private ILogger _logger;

        public ReviewBlockTree(EthereumRunnerContext context)
        {
            _context = context;
            _logger = _context.LogManager.GetClassLogger();
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            if (_context.Config<IInitConfig>().ProcessingEnabled)
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
            ISyncConfig syncConfig = _context.Config<ISyncConfig>();
            if (!syncConfig.SynchronizationEnabled)
            {
                return;
            }
            
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));

            if (!syncConfig.FastSync && !syncConfig.BeamSync)
            {
                DbBlocksLoader loader = new DbBlocksLoader(_context.BlockTree, _logger);
                await _context.BlockTree.Accept(loader, cancellationToken).ContinueWith(t =>
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
                StartupBlockTreeFixer fixer = new StartupBlockTreeFixer(_context.Config<ISyncConfig>(), _context.BlockTree, _logger);
                await _context.BlockTree.Accept(fixer, cancellationToken).ContinueWith(t =>
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