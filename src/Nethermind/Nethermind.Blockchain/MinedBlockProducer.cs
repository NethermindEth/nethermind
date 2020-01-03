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
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class MinedBlockProducer : BaseBlockProducer
    {
        private readonly IBlockchainProcessor _processor;
        private readonly IBlockTree _blockTree;
        private readonly IDifficultyCalculator _difficultyCalculator;
        private readonly object _syncToken = new object();
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public MinedBlockProducer(
            IPendingTransactionSelector pendingTransactionSelector,
            IBlockchainProcessor processor,
            ISealer sealer,
            IBlockTree blockTree,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            ILogManager logManager,
            IDifficultyCalculator difficultyCalculator) 
            : base(pendingTransactionSelector, processor, sealer, blockTree, stateProvider, timestamper, logManager)
        {
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));;
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));;
            _difficultyCalculator = difficultyCalculator ?? throw new ArgumentNullException(nameof(difficultyCalculator));
        }

        private void BlockTreeOnNewBestSuggestedBlock(object sender, BlockEventArgs e)
        {
            lock (_syncToken)
            {
                _cancellationTokenSource?.Cancel();
            }
        }

        private async void OnBlockProcessorQueueEmpty(object sender, EventArgs e)
        {
            CancellationToken token;
            lock (_syncToken)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                token = _cancellationTokenSource.Token;
            }

            await base.TryProduceNewBlock(token);
        }

        public override void Start()
        {
            _processor.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
            _blockTree.NewBestSuggestedBlock += BlockTreeOnNewBestSuggestedBlock;
        }

        public override async Task StopAsync()
        {
            _processor.ProcessingQueueEmpty -= OnBlockProcessorQueueEmpty;
            _blockTree.NewBestSuggestedBlock -= BlockTreeOnNewBestSuggestedBlock;
            
            lock (_syncToken)
            {
                _cancellationTokenSource?.Cancel();
            }
            
            await Task.CompletedTask;
        }

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp)
        {
            Block parentBlock = _blockTree.FindBlock(parent.Hash, BlockTreeLookupOptions.None);
            return _difficultyCalculator.Calculate(parent.Difficulty, parent.Timestamp, timestamp, parent.Number + 1, parentBlock.Ommers.Length > 0);
        }
    }
}