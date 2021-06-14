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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;

namespace Nethermind.Core.Test.Blockchain
{
    public class TestBlockProducer : LoopBlockProducerBase, ITestBlockProducer
    {
        public TestBlockProducer(
            ITxSource transactionSource,
            IBlockchainProcessor processor,
            IStateProvider stateProvider,
            ISealer sealer,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            ITimestamper timestamper,
            ISpecProvider specProvider,
            ILogManager logManager)
            : base(
                transactionSource,
                processor,
                sealer,
                blockTree,
                blockProcessingQueue,
                stateProvider,
                timestamper,
                new FollowOtherMiners(specProvider),
                specProvider,
                logManager,
                "test producer")
        {
        }

        public Block LastProducedBlock { get; private set; }
        public event EventHandler<BlockEventArgs> LastProducedBlockChanged;

        private readonly SemaphoreSlim _newBlockArrived = new SemaphoreSlim(0);
        private BlockHeader? _blockParent = null;
        
        public BlockHeader? BlockParent
        {
            get
            {
                return _blockParent ?? BlockTree.Head?.Header;
            }
            set
            {
                _blockParent = value;
            }
        }

        protected override BlockHeader? GetProducedBlockParent(BlockHeader? parentHeader) => parentHeader ?? BlockParent;

        public Task<bool> BuildNewBlock()
        {
            _newBlockArrived.Release(1);
            return Task.FromResult(true);
        }

        protected override async ValueTask ProducerLoop()
        {
            _lastProducedBlockDateTime = DateTime.UtcNow;
            while (!LoopCancellationTokenSource.IsCancellationRequested)
            {
                await _newBlockArrived.WaitAsync(LoopCancellationTokenSource.Token);
                try
                {
                    await TryProduceNewBlock(LoopCancellationTokenSource.Token);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
                // Console.WriteLine($"Produce new block result -> {result}");
            }
        }

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp)
        {
            return 1;
        }

        protected override async Task<Block> SealBlock(Block block, BlockHeader parent, CancellationToken token)
        {
            var result = await base.SealBlock(block, parent, token);
            LastProducedBlock = result;
            LastProducedBlockChanged?.Invoke(this, new BlockEventArgs(block));
            return result;
        }
    }
}
