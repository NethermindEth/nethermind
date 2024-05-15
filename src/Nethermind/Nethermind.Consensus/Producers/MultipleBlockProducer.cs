// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Producers
{
    public abstract class MultipleBlockProducer<T> : IBlockProducer where T : IBlockProducerInfo
    {
        private readonly IBestBlockPicker _bestBlockPicker;
        private readonly T[] _blockProducers;
        private readonly ILogger _logger;

        protected MultipleBlockProducer(
            IBestBlockPicker bestBlockPicker,
            ILogManager logManager,
            params T[] blockProducers)
        {
            if (blockProducers.Length == 0) throw new ArgumentException("Collection cannot be empty.", nameof(blockProducers));
            _bestBlockPicker = bestBlockPicker;
            _blockProducers = blockProducers;
            _logger = logManager.GetClassLogger();
        }

        public async Task<Block?> BuildBlock(BlockHeader? parentHeader, CancellationToken? token, IBlockTracer? blockTracer = null,
            PayloadAttributes? payloadAttributes = null)
        {
            Task<Block?>[] produceTasks = new Task<Block?>[_blockProducers.Length];
            for (int i = 0; i < _blockProducers.Length; i++)
            {
                T blockProducerInfo = _blockProducers[i];
                if (!blockProducerInfo.Condition.CanProduce(parentHeader))
                {
                    produceTasks[i] = Task.FromResult<Block?>(null);
                    continue;
                }
                produceTasks[i] = blockProducerInfo.BlockProducer.BuildBlock(parentHeader, token, blockProducerInfo.BlockTracer);
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
            }

            return bestBlock;
        }

        public interface IBestBlockPicker
        {
            Block? GetBestBlock(IEnumerable<(Block? Block, T BlockProducerInfo)> blocks);
        }
    }
}
