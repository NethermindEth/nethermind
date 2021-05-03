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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class Eth2BlockProducer : BlockProducerBase, IManualBlockProducer
    {
        private int _stated;
        private readonly SemaphoreSlim _locker = new(1, 1);
        
        public Eth2BlockProducer(ITxSource txSource,
            IBlockchainProcessor processor,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            IStateProvider stateProvider,
            IGasLimitCalculator gasLimitCalculator,
            ISigner signer,
            ITimestamper timestamper,
            ILogManager logManager) 
            : base(txSource, processor, new Eth2SealEngine(signer), blockTree, blockProcessingQueue, stateProvider, gasLimitCalculator, timestamper, logManager)
        {
        }

        public override void Start() => Interlocked.Exchange(ref _stated, 1);

        public override Task StopAsync()
        {
            Interlocked.Exchange(ref _stated, 0);
            return Task.CompletedTask;
        }

        public async Task<BlockProducedContext> TryProduceBlock(BlockHeader parentHeader, CancellationToken cancellationToken = default)
        {
            await _locker.WaitAsync(cancellationToken);
            try
            {
                Block? block = await TryProduceNewBlock(cancellationToken, parentHeader);
                return new BlockProducedContext(block, StateProvider);
            }
            finally
            {
                _locker.Release();
            }
        }

        protected override bool IsRunning() => _stated == 1;

        protected override void ConsumeProducedBlock(Block block) { }

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp) => UInt256.One;
        
        protected override Block PrepareBlock(BlockHeader parent)
        {
            Block block = base.PrepareBlock(parent);
            block.Header.MixHash = Keccak.Zero;
            block.Header.ExtraData = Bytes.Empty;
            return block;
        }
    }
}
