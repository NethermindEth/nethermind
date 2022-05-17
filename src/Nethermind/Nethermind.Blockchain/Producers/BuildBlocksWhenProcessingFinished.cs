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
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Producers
{
    public class BuildBlocksWhenProcessingFinished : IManualBlockProductionTrigger, IDisposable
    {
        private readonly object _locker = new();
        private readonly IBlockProcessingQueue _blockProcessingQueue;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private CancellationTokenSource? _cancellationTokenSource;
        
        public BuildBlocksWhenProcessingFinished(
            IBlockProcessingQueue blockProcessingQueue,
            IBlockTree blockTree,
            ILogManager logManager)
        {
            _blockProcessingQueue = blockProcessingQueue;
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger();

            _blockProcessingQueue.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
            _blockTree.NewBestSuggestedBlock += OnNewBestSuggestedBlock;
        }

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;

        private void OnNewBestSuggestedBlock(object? sender, BlockEventArgs e)
        {
            lock (_locker)
            {
                TryCancelBlockProduction();
            }
        }
        
        private void OnBlockProcessorQueueEmpty(object? sender, EventArgs e)
        {
            BuildBlock();
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
            _blockTree.NewBestSuggestedBlock -= OnNewBestSuggestedBlock;
            _blockProcessingQueue.ProcessingQueueEmpty -= OnBlockProcessorQueueEmpty;
        }
    }
}
