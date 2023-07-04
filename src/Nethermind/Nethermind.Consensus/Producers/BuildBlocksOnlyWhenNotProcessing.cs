// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Producers
{
    public class BuildBlocksOnlyWhenNotProcessing : IBlockProductionTrigger, IDisposable, IAsyncDisposable
    {
        public const int ChainNotYetProcessedMillisecondsDelay = 100;
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

        private void OnTriggerBlockProduction(object? sender, BlockProductionEventArgs e) => InvokeTriggerBlockProduction(e);

        private void InvokeTriggerBlockProduction(BlockProductionEventArgs e)
        {
            if (CanTriggerBlockProduction)
            {
                // if we can trigger production lets do it directly, this should be most common case
                TriggerBlockProduction?.Invoke(this, e);
            }
            else
            {
                // otherwise set delayed production task
                // we need to clone event args as its BlockProductionTask will be awaited in delayed production task
                e.BlockProductionTask = InvokeTriggerBlockProductionDelayed(e.Clone());
            }
        }

        private bool CanTriggerBlockProduction => _canProduce == 1 && _blockProcessingQueue.IsEmpty;


        private async Task<Block?> InvokeTriggerBlockProductionDelayed(BlockProductionEventArgs e)
        {
            CancellationToken cancellationToken = e.CancellationToken;
            // retry production until its allowed or its cancelled
            while (!CanTriggerBlockProduction)
            {
                if (_logger.IsDebug) _logger.Debug($"Delaying producing block, chain not processed yet. BlockProcessingQueue count {_blockProcessingQueue.Count}.");
                await Task.Delay(ChainNotYetProcessedMillisecondsDelay, cancellationToken);
            }

            TriggerBlockProduction?.Invoke(this, e);

            return await e.BlockProductionTask;
        }

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;

        public void Dispose()
        {
            DetachEvents();

            if (_blockProductionTrigger is IDisposable disposableTrigger)
            {
                disposableTrigger.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            DetachEvents();

            if (_blockProductionTrigger is IAsyncDisposable disposableTrigger)
            {
                await disposableTrigger.DisposeAsync();
            }
        }

        private void DetachEvents()
        {
            _blockProductionTrigger.TriggerBlockProduction -= OnTriggerBlockProduction;
            _blockTree.NewBestSuggestedBlock -= BlockTreeOnNewBestSuggestedBlock;
            _blockProcessingQueue.ProcessingQueueEmpty -= OnBlockProcessorQueueEmpty;
        }
    }
}
