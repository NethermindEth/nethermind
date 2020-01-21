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

using System;
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
        private const int ChainNotYetProcessedMillisecondsDelay = 100;
        private readonly string _name;
        private Task _producerTask;
        private readonly CancellationTokenSource _loopCancellationTokenSource = new CancellationTokenSource();
        private readonly CancellationTokenSource _stepCancellationTokenSource = new CancellationTokenSource();
        private bool _canProduce;

        protected BaseLoopBlockProducer(
            IPendingTransactionSelector pendingTransactionSelector,
            IBlockchainProcessor processor,
            ISealer sealer,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            ILogManager logManager,
            string name) 
            : base(pendingTransactionSelector, processor, sealer, blockTree, blockProcessingQueue, stateProvider, timestamper, logManager)
        {
            _name = name;
        }

        public override void Start()
        {
            BlockProcessingQueue.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
            BlockTree.NewBestSuggestedBlock += BlockTreeOnNewBestSuggestedBlock;
            
            _producerTask = Task.Run(ProducerLoop, _loopCancellationTokenSource.Token).ContinueWith(t =>
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
            BlockProcessingQueue.ProcessingQueueEmpty -= OnBlockProcessorQueueEmpty;
            BlockTree.NewBestSuggestedBlock -= BlockTreeOnNewBestSuggestedBlock;
            
            _loopCancellationTokenSource?.Cancel();
            _stepCancellationTokenSource?.Cancel();
            await (_producerTask ?? Task.CompletedTask);
        }
        
        protected virtual async ValueTask ProducerLoop()
        {
            while (!_loopCancellationTokenSource.IsCancellationRequested)
            {
                if (_canProduce && BlockProcessingQueue.IsEmpty)
                {
                    await ProducerLoopStep(_stepCancellationTokenSource.Token);
                }
                else
                {
                    if (Logger.IsDebug) Logger.Debug("Delaying producing block, chain not processed yet.");
                    await Task.Delay(ChainNotYetProcessedMillisecondsDelay);
                }
            }
        }

        protected virtual async ValueTask ProducerLoopStep(CancellationToken cancellationToken)
        {
            await TryProduceNewBlock(cancellationToken);
        }
        
        private void BlockTreeOnNewBestSuggestedBlock(object sender, BlockEventArgs e)
        {
            _canProduce = false;
        }

        private void OnBlockProcessorQueueEmpty(object sender, EventArgs e)
        {
            _canProduce = true;
        }
    }
}