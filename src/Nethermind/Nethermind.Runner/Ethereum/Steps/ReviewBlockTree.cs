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

using System.Threading.Tasks;
using Nethermind.Blockchain;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(StartBlockProducer))]
    public class ReviewBlockTree : IStep
    {
        private readonly EthereumRunnerContext _context;

        public ReviewBlockTree(EthereumRunnerContext context)
        {
            _context = context;
        }

        public async Task Execute()
        {
            if (_context.Config<IInitConfig>().ProcessingEnabled)
            {
#pragma warning disable 4014
                RunBlockTreeInitTasks();
#pragma warning restore 4014
            }
            else
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn($"Shutting down the blockchain processor due to {nameof(InitConfig)}.{nameof(InitConfig.ProcessingEnabled)} set to false");
                await _context.BlockchainProcessor.StopAsync();
            }
        }
        
        private async Task RunBlockTreeInitTasks()
        {
            ISyncConfig syncConfig = _context.Config<ISyncConfig>(); 
            if (!syncConfig.SynchronizationEnabled)
            {
                return;
            }

            if (!syncConfig.FastSync)
            {
                await _context.BlockTree.LoadBlocksFromDb(_context.RunnerCancellation.Token, null).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_context.Logger.IsError) _context.Logger.Error("Loading blocks from the DB failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (_context.Logger.IsWarn) _context.Logger.Warn("Loading blocks from the DB canceled.");
                    }
                });
            }
            else
            {
                await _context.BlockTree.FixFastSyncGaps(_context.RunnerCancellation.Token).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_context.Logger.IsError) _context.Logger.Error("Fixing gaps in DB failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (_context.Logger.IsWarn) _context.Logger.Warn("Fixing gaps in DB canceled.");
                    }
                });
            }
        }
    }
}