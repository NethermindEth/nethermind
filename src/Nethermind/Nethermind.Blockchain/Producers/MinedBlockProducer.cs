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
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Blockchain.Producers
{
    public class MinedBlockProducer : BaseBlockProducer
    {
        private readonly IDifficultyCalculator _difficultyCalculator;
        private readonly object _syncToken = new object();
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public MinedBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            ISealer sealer,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            ILogManager logManager,
            IDifficultyCalculator difficultyCalculator) 
            : base(txSource, processor, sealer, blockTree, blockProcessingQueue, stateProvider, timestamper, logManager)
        {
            _difficultyCalculator = difficultyCalculator ?? throw new ArgumentNullException(nameof(difficultyCalculator));
        }

        private void BlockTreeOnNewBestSuggestedBlock(object sender, BlockEventArgs e)
        {
            lock (_syncToken)
            {
                _cancellationTokenSource?.Cancel();
            }
        }

        private void OnBlockProcessorQueueEmpty(object sender, EventArgs e)
        {
            CancellationToken token;
            lock (_syncToken)
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource = new CancellationTokenSource();
                token = _cancellationTokenSource.Token;
            }

            TryProduceNewBlock(token);
        }

        public override void Start()
        {
            BlockProcessingQueue.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
            BlockTree.NewBestSuggestedBlock += BlockTreeOnNewBestSuggestedBlock;
        }

        public override async Task StopAsync()
        {
            BlockProcessingQueue.ProcessingQueueEmpty -= OnBlockProcessorQueueEmpty;
            BlockTree.NewBestSuggestedBlock -= BlockTreeOnNewBestSuggestedBlock;
            
            lock (_syncToken)
            {
                _cancellationTokenSource?.Cancel();
            }
            
            await Task.CompletedTask;
        }

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp)
        {
            Block parentBlock = BlockTree.FindBlock(parent.Hash, BlockTreeLookupOptions.None);
            return _difficultyCalculator.Calculate(parent.Difficulty, parent.Timestamp, timestamp, parent.Number + 1, parentBlock.Ommers.Length > 0);
        }
    }
}