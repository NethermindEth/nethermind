// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Producers
{
    public abstract class MultipleBlockProducer<T> : IBlockProducer where T : IBlockProducerInfo
    {
        private readonly IBlockProductionTrigger _blockProductionTrigger;
        private readonly IBestBlockPicker _bestBlockPicker;
        private readonly T[] _blockProducers;
        private readonly ILogger _logger;

        protected MultipleBlockProducer(
            IBlockProductionTrigger blockProductionTrigger,
            IBestBlockPicker bestBlockPicker,
            ILogManager logManager,
            params T[] blockProducers)
        {
            if (blockProducers.Length == 0) throw new ArgumentException("Collection cannot be empty.", nameof(blockProducers));
            _blockProductionTrigger = blockProductionTrigger;
            _bestBlockPicker = bestBlockPicker;
            _blockProducers = blockProducers;
            _logger = logManager.GetClassLogger();
        }

        public Task Start()
        {
            for (int index = 0; index < _blockProducers.Length; index++)
            {
                IBlockProducer blockProducer = _blockProducers[index].BlockProducer;
                blockProducer.Start();
            }

            _blockProductionTrigger.TriggerBlockProduction += OnBlockProduction;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _blockProductionTrigger.TriggerBlockProduction -= OnBlockProduction;

            IList<Task> stopTasks = new List<Task>();
            for (int index = 0; index < _blockProducers.Length; index++)
            {
                IBlockProducer blockProducer = _blockProducers[index].BlockProducer;
                stopTasks.Add(blockProducer.StopAsync());
            }

            return Task.WhenAll(stopTasks);
        }

        public bool IsProducingBlocks(ulong? maxProducingInterval)
        {
            for (int index = 0; index < _blockProducers.Length; index++)
            {
                IBlockProducer blockProducer = _blockProducers[index].BlockProducer;
                if (blockProducer.IsProducingBlocks(maxProducingInterval))
                {
                    return true;
                }
            }

            return false;
        }

        public event EventHandler<BlockEventArgs>? BlockProduced;

        private void OnBlockProduction(object? sender, BlockProductionEventArgs e)
        {
            e.BlockProductionTask = TryProduceBlock(e.ParentHeader, e.CancellationToken);
        }

        private async Task<Block?> TryProduceBlock(BlockHeader? parentHeader, CancellationToken cancellationToken = default)
        {
            Task<Block?>[] produceTasks = new Task<Block?>[_blockProducers.Length];
            for (int i = 0; i < _blockProducers.Length; i++)
            {
                T blockProducerInfo = _blockProducers[i];
                produceTasks[i] = blockProducerInfo.BlockProductionTrigger.BuildBlock(parentHeader, cancellationToken, blockProducerInfo.BlockTracer);
            }

            IEnumerable<(Block? Block, T BlockProducer)> blocksWithProducers;

            try
            {
                Block?[] blocks = await Task.WhenAll(produceTasks);
                blocksWithProducers = blocks.Zip(_blockProducers);
            }
            catch (OperationCanceledException)
            {
                blocksWithProducers = produceTasks
                    .Zip(_blockProducers)
                    .Where(t => t.First.IsCompletedSuccessfully)
                    .Select(t => (t.First.Result, t.Second));
            }

            Block? bestBlock = _bestBlockPicker.GetBestBlock(blocksWithProducers);
            if (bestBlock is not null)
            {
                if (produceTasks.Count(t => t.IsCompletedSuccessfully && t.Result is not null) > 1)
                {
                    if (_logger.IsInfo) _logger.Info($"Picked block {bestBlock} to be included to the chain.");
                }

                BlockProduced?.Invoke(this, new BlockEventArgs(bestBlock));
            }

            return bestBlock;
        }

        public interface IBestBlockPicker
        {
            Block? GetBestBlock(IEnumerable<(Block? Block, T BlockProducerInfo)> blocks);
        }
    }
}
