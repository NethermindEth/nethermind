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

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Ethash
{
    public class MinedBlockProducer : BlockProducerBase
    {
        private bool _isRunning;
        private readonly IDifficultyCalculator _difficultyCalculator;
        private readonly object _syncToken = new();
        private CancellationTokenSource _cancellationTokenSource = new();

        public MinedBlockProducer(ITxSource txSource,
            IBlockchainProcessor processor,
            ISealer sealer,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            IStateProvider stateProvider,
            IGasLimitCalculator gasLimitCalculator,
            ITimestamper timestamper,
            ISpecProvider specProvider,
            ILogManager logManager,
            IDifficultyCalculator difficultyCalculator) 
            : base(
                txSource,
                processor,
                sealer,
                blockTree,
                blockProcessingQueue,
                stateProvider,
                gasLimitCalculator,
                timestamper,
                specProvider,
                logManager)
        {
            _difficultyCalculator = difficultyCalculator ?? throw new ArgumentNullException(nameof(difficultyCalculator));
        }

        private void BlockTreeOnNewBestSuggestedBlock(object sender, BlockEventArgs e)
        {
            lock (_syncToken)
            {
                _cancellationTokenSource.Cancel();
            }
        }

        private void OnBlockProcessorQueueEmpty(object sender, EventArgs e)
        {
            CancellationToken token;
            lock (_syncToken)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource = new CancellationTokenSource();
                token = _cancellationTokenSource.Token;
            }

            TryProduceNewBlock(token).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    Block? block = t.Result;
                    if (block is not null)
                    {
                        ConsumeProducedBlock(block);
                    }
                }
            }, token);
        }

        public override void Start()
        {
            BlockProcessingQueue.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
            BlockTree.NewBestSuggestedBlock += BlockTreeOnNewBestSuggestedBlock;
            _lastProducedBlockDateTime = DateTime.UtcNow;
            _isRunning = true;
        }

        public override async Task StopAsync()
        {
            BlockProcessingQueue.ProcessingQueueEmpty -= OnBlockProcessorQueueEmpty;
            BlockTree.NewBestSuggestedBlock -= BlockTreeOnNewBestSuggestedBlock;
            
            lock (_syncToken)
            {
                _cancellationTokenSource.Cancel();
            }

            _isRunning = false;
            await Task.CompletedTask;
        }

        protected override bool IsRunning() => _isRunning;

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp)
        {
            if (parent.Hash is null)
            {
                throw new InvalidDataException("parent.Hash is null when calculating difficulty");
            }
            
            Block? parentBlock = BlockTree.FindBlock(parent.Hash, BlockTreeLookupOptions.None);
            if (parentBlock is null)
            {
                throw new InvalidDataException("parentBlock is null when calculating difficulty");
            }
            
            return _difficultyCalculator.Calculate(
                parent.Difficulty, parent.Timestamp, timestamp, parent.Number + 1, parentBlock.Ommers.Length > 0);
        }
    }
}
