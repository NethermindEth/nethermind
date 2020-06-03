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
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Logging;
using Block = Nethermind.Core.Block;

namespace Nethermind.PubSub
{
    public class BlocksSubscription : ISubscription
    {
        private readonly IBlockProcessor _blockProcessor;
        private readonly IEnumerable<IProducer> _producers;
        private readonly ILogger _logger;

        public BlocksSubscription(IEnumerable<IProducer> producers, IBlockProcessor blockProcessor, ILogManager logManager)
        {
            _producers = producers ?? throw new ArgumentNullException(nameof(producers));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _logger = logManager.GetClassLogger();
            _blockProcessor.BlockProcessed += OnBlockProcessed;
            if (_logger.IsInfo) _logger.Info("New data subscription started");
        }

        private async void OnBlockProcessed(object sender, BlockProcessedEventArgs e)
            => await PublishBlockAsync(e.Block);

        public void Dispose()
        {
            _blockProcessor.BlockProcessed -= OnBlockProcessed;
            if (_logger.IsInfo) _logger.Info("Data subscription closed");
        }

        private async Task PublishBlockAsync(Block block)
        {
            foreach (var producer in _producers)
            {
                try
                {
                    await producer.PublishAsync(block);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
        }
    }
}