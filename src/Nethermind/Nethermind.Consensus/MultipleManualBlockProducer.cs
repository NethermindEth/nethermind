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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Consensus
{
    public abstract class MultipleManualBlockProducer : IManualBlockProducer
    {
        private readonly IManualBlockProducer[] _blockProducers;

        protected MultipleManualBlockProducer(params IManualBlockProducer[] blockProducers)
        {
            if (blockProducers.Length == 0) throw new ArgumentException("Collection cannot be empty.", nameof(blockProducers));
            _blockProducers = blockProducers;
        }
        
        public void Start()
        {
            for (int i = 0; i < _blockProducers.Length; i++)
            {
                _blockProducers[i].Start();
            }
        }

        public Task StopAsync()
        {
            IList<Task> stopTasks = new List<Task>();
            for (int i = 0; i < _blockProducers.Length; i++)
            {
                stopTasks.Add(_blockProducers[i].StopAsync());
            }

            return Task.WhenAll(stopTasks);
        }

        public bool IsProducingBlocks(ulong? maxProducingInterval)
        {
            for (int i = 0; i < _blockProducers.Length; i++)
            {
                if (_blockProducers[i].IsProducingBlocks(maxProducingInterval))
                {
                    return true;
                }
            }

            return false;
        }

        public ITimestamper Timestamper => _blockProducers[0].Timestamper;

        public async Task<Block?> TryProduceBlock(BlockHeader parentHeader, CancellationToken cancellationToken = default)
        {
            Task<Block?>[] produceTasks = new Task<Block?>[_blockProducers.Length];
            for (int i = 0; i < _blockProducers.Length; i++)
            {
                produceTasks[i] = _blockProducers[i].TryProduceBlock(parentHeader, cancellationToken);
            }
           
            IEnumerable<(Block? Block, IManualBlockProducer BlockProducer)> blocksWithProducers;
            
            try
            {
                Block?[] blocks = await Task.WhenAll(produceTasks);
                blocksWithProducers = blocks.Zip(_blockProducers);
            }
            catch (OperationCanceledException)
            {
                blocksWithProducers  = produceTasks
                    .Zip(_blockProducers)
                    .Where(t => t.First.IsCompletedSuccessfully)
                    .Select(t => (t.First.Result, t.Second));
            }
            
            return GetBestBlock(blocksWithProducers);
        }

        protected abstract Block? GetBestBlock(IEnumerable<(Block? Block, IManualBlockProducer BlockProducer)> blocks);
    }
}
