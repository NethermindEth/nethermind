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
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public abstract class BaseLoopBlockProducer : BaseBlockProducer
    {
        private readonly string _name;
        private Task _producerTask;
        protected readonly CancellationTokenSource CancellationTokenSource = new CancellationTokenSource();
        
        protected BaseLoopBlockProducer(
            IPendingTransactionSelector pendingTransactionSelector,
            IBlockchainProcessor processor,
            ISealer sealer,
            IBlockTree blockTree,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            ILogManager logManager,
            string name) 
            : base(pendingTransactionSelector, processor, sealer, blockTree, stateProvider, timestamper, logManager)
        {
            _name = name;
        }

        public override void Start()
        {
            _producerTask = Task.Run(ProducerLoop, CancellationTokenSource.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (Logger.IsError) Logger.Error($"{_name} block producer encountered an exception.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (Logger.IsDebug) Logger.Debug($"{_name} block producer stopped.");
                }
                else if (t.IsCompleted)
                {
                    if (Logger.IsDebug) Logger.Debug($"{_name} block producer complete.");
                }
            });
        }
        
        public override async Task StopAsync()
        {
            CancellationTokenSource?.Cancel();
            await (_producerTask ?? Task.CompletedTask);
        }
        
        protected virtual async ValueTask ProducerLoop()
        {
            while (!CancellationTokenSource.IsCancellationRequested)
            {
                await ProducerLoopStep();
            }
        }

        protected virtual async ValueTask ProducerLoopStep()
        {
            await TryProduceNewBlock(CancellationTokenSource.Token);
        }
    }
}