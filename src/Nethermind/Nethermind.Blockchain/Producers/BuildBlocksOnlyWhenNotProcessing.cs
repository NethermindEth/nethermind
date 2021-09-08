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
using Nethermind.Logging;

namespace Nethermind.Blockchain.Producers
{
    public class BuildBlocksOnlyWhenNotProcessing : IBlockProductionTrigger, IDisposable
    {
        private const int ChainNotYetProcessedMillisecondsDelay = 100;
        private readonly IBlockProductionTrigger _blockProductionTrigger;
        private readonly IBlockProcessingQueue _blockProcessingQueue;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private int _canProduce;

        public BuildBlocksOnlyWhenNotProcessing(
            IBlockProductionTrigger blockProductionTrigger,
            IBlockProcessingQueue blockProcessingQueue,
            IBlockTree blockTree,
            ILogManager logManager,
            bool waitForInitialSync = true)
        {
            _blockProductionTrigger = blockProductionTrigger;
            _blockProcessingQueue = blockProcessingQueue;
            _blockTree = blockTree;
            _canProduce = waitForInitialSync ? 0 : 1;
            _logger = logManager.GetClassLogger();

            _blockTree.NewBestSuggestedBlock += BlockTreeOnNewBestSuggestedBlock;
            _blockProcessingQueue.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
            _blockProductionTrigger.TriggerBlockProduction += OnTriggerBlockProduction;
        }

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
        }

        private void OnBlockProcessorQueueEmpty(object? sender, EventArgs e)
        {
            Interlocked.Exchange(ref _canProduce, 1);
            Interlocked.Exchange(ref Metrics.CanProduceBlocks, 1);
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Can produce blocks, current best suggested {_blockTree.BestSuggestedHeader}" +
                    $"{Environment.NewLine}current head {_blockTree.Head}{Environment.NewLine}{new StackTrace()}");        

        }

        private void OnTriggerBlockProduction(object? sender, BlockProductionEventArgs e)
        {
            if (_canProduce == 1 && _blockProcessingQueue.IsEmpty)
            {
                TriggerBlockProduction?.Invoke(this, e);
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Delaying producing block, chain not processed yet. BlockProcessingQueue count {_blockProcessingQueue.Count}.");
                e.BlockProductionTask = Task.Delay(ChainNotYetProcessedMillisecondsDelay, e.CancellationToken)
                    .ContinueWith(t => (Block?)null);
            }
        }

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;
        
        public void Dispose()
        {
            _blockProductionTrigger.TriggerBlockProduction -= OnTriggerBlockProduction;
            _blockTree.NewBestSuggestedBlock -= BlockTreeOnNewBestSuggestedBlock;
            _blockProcessingQueue.ProcessingQueueEmpty -= OnBlockProcessorQueueEmpty;

            if (_blockProductionTrigger is IDisposable disposableTrigger)
            {
                disposableTrigger.Dispose();
            }
        }
    }
}
