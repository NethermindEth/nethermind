//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Producers
{
    public class BuildBlocksInALoop : IBlockProductionTrigger, IDisposable
    {
        private readonly CancellationTokenSource _loopCancellationTokenSource = new();
        protected ILogger Logger { get; }
        
        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;

        public BuildBlocksInALoop(ILogManager logManager)
        {
            Logger = logManager.GetClassLogger();
            Task.Run(ProducerLoop, _loopCancellationTokenSource.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (Logger.IsError) Logger.Error($"Block producer encountered an exception.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (Logger.IsDebug) Logger.Debug($"Block producer stopped.");
                }
                else if (t.IsCompleted)
                {
                    if (Logger.IsDebug) Logger.Debug($"Block producer complete.");
                }
            });
        }
        
        private async Task ProducerLoop()
        {
            while (!_loopCancellationTokenSource.IsCancellationRequested)
            {
                await ProducerLoopStep(_loopCancellationTokenSource.Token);
            }
        }

        protected virtual async Task ProducerLoopStep(CancellationToken token)
        {
            BlockProductionEventArgs args = new(cancellationToken:token);
            TriggerBlockProduction?.Invoke(this, args);
            await args.BlockProductionTask;
        }
        
        public void Dispose()
        {
            _loopCancellationTokenSource.Cancel();
        }
    }
}
