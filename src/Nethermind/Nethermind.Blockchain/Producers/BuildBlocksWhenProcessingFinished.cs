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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Producers
{
    public class BuildBlocksWhenProcessingFinished : IManualBlockProductionTrigger, IDisposable
    {
        public const int ChainNotYetProcessedMillisecondsDelay = 100;
        private readonly object _locker = new();
        private readonly IBlockProcessingQueue _blockProcessingQueue;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private CancellationTokenSource? _cancellationTokenSource;
        private int _canProduce;
        private bool CanTriggerBlockProduction => _canProduce == 1 && _blockProcessingQueue.IsEmpty;
        
        public BuildBlocksWhenProcessingFinished(
            IBlockProcessingQueue blockProcessingQueue,
            IBlockTree blockTree,
            ILogManager logManager,
            bool waitForInitialSync = true)
        {
            _blockProcessingQueue = blockProcessingQueue;
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger();
            _canProduce = waitForInitialSync ? 0 : 1;

            _blockProcessingQueue.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
            _blockTree.NewBestSuggestedBlock += BlockTreeOnNewBestSuggestedBlock;
        }

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;
        
        private void BlockTreeOnNewBestSuggestedBlock(object sender, BlockEventArgs e)
        {
            if (_blockTree.Head?.Hash != e.Block.Hash)
            {
                Interlocked.Exchange(ref _canProduce, 0);
                Interlocked.Exchange(ref Metrics.CanProduceBlocks, 0);
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"Can not produce a block new best suggested {_blockTree.BestSuggestedHeader?.ToString(BlockHeader.Format.FullHashAndNumber)}" +
                        $"{Environment.NewLine}{new StackTrace()}");
            }
            else
            {
                Interlocked.Exchange(ref Metrics.CanProduceBlocks, 1);
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"Can produce blocks, a block new best suggested {_blockTree.BestSuggestedHeader?.ToString(BlockHeader.Format.FullHashAndNumber)}" +
                        $"{Environment.NewLine}{new StackTrace()} is already processed.");
            }

            InvokeTriggerBlockProduction(new BlockProductionEventArgs(e.Block.Header));
        }

        private void OnBlockProcessorQueueEmpty(object? sender, EventArgs e)
        {
            Interlocked.Exchange(ref _canProduce, 1);
            Interlocked.Exchange(ref Metrics.CanProduceBlocks, 1);
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Can produce blocks, current best suggested {_blockTree.BestSuggestedHeader}" +
                    $"{Environment.NewLine}current head {_blockTree.Head}{Environment.NewLine}{new StackTrace()}"); 
            InvokeTriggerBlockProduction(new BlockProductionEventArgs());
        }

        private void TryCancelBlockProduction()
        {
            CancellationTokenSource? tokenSource = Interlocked.Exchange(ref _cancellationTokenSource, null);
            if (tokenSource is not null)
            {
                tokenSource.Cancel();
                tokenSource.Dispose();
            }
        }
        
        private void InvokeTriggerBlockProduction(BlockProductionEventArgs e)
        {
            if (CanTriggerBlockProduction)
            {
                // if we can trigger production lets do it directly, this should be most common case
                BuildBlock(e.ParentHeader, e.CancellationToken, e.BlockTracer);
            }
            else
            {
                // otherwise set delayed production task
                // we need to clone event args as its BlockProductionTask will be awaited in delayed production task
                e.BlockProductionTask = InvokeTriggerBlockProductionDelayed(e.Clone());
            }
        }
        
        private async Task<Block?> InvokeTriggerBlockProductionDelayed(BlockProductionEventArgs e)
        {
            CancellationToken cancellationToken = e.CancellationToken;
            // retry production until its allowed or its cancelled
            while (!CanTriggerBlockProduction && !cancellationToken.IsCancellationRequested)
            {
                if (_logger.IsDebug) _logger.Debug($"Delaying producing block, chain not processed yet. BlockProcessingQueue count {_blockProcessingQueue.Count}.");
                await Task.Delay(ChainNotYetProcessedMillisecondsDelay, cancellationToken);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await BuildBlock(e.ParentHeader, e.CancellationToken, e.BlockTracer);
            }

            return await e.BlockProductionTask;
        }

        public Task<Block?> BuildBlock(BlockHeader? parentHeader = null, CancellationToken? cancellationToken = null,  IBlockTracer? blockTracer = null)
        {
            lock (_locker)
            {
                TryCancelBlockProduction();
                _cancellationTokenSource = cancellationToken is not null 
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken.Value) 
                    : new CancellationTokenSource();
                
                BlockProductionEventArgs eventArgs = new(parentHeader, _cancellationTokenSource.Token);
                TriggerBlockProduction?.Invoke(this, eventArgs);
                return eventArgs.BlockProductionTask;
            }
        }

        public void Dispose()
        {
            TryCancelBlockProduction();
            _blockTree.NewBestSuggestedBlock -= BlockTreeOnNewBestSuggestedBlock;
            _blockProcessingQueue.ProcessingQueueEmpty -= OnBlockProcessorQueueEmpty;
        }
    }
}
