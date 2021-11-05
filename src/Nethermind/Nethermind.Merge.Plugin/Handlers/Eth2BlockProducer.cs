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
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class MergeBlockProducer : IBlockProducer
    {
        private readonly IBlockProducer? _preMergeProducer;
        private readonly IBlockProducer _eth2BlockProducer;
        private readonly IPoSSwitcher _poSSwitcher;
        private bool HasPreMergeProducer => _preMergeProducer != null;

        // TODO: remove this
        public ITimestamper Timestamper => _preMergeProducer?.Timestamper;

        public MergeBlockProducer(IBlockProducer? preMergeProducer, IBlockProducer? postMergeBlockProducer, IPoSSwitcher? poSSwitcher)
        {
            _preMergeProducer = preMergeProducer;
            _eth2BlockProducer = postMergeBlockProducer ?? throw new ArgumentNullException(nameof(postMergeBlockProducer));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _poSSwitcher.SwitchHappened += OnSwitchHappened;
            if (HasPreMergeProducer)
                _preMergeProducer!.BlockProduced += OnBlockProduced;
            
            postMergeBlockProducer.BlockProduced += OnBlockProduced;
        }

        private void OnBlockProduced(object? sender, BlockEventArgs e)
        {
            BlockProduced?.Invoke(this, e);
        }

        private void OnSwitchHappened(object? sender, EventArgs e)
        {
            _preMergeProducer?.StopAsync();
        }

        public async Task Start()
        {
            await _eth2BlockProducer.Start();
            if (_poSSwitcher.HasEverBeenInPos() == false && HasPreMergeProducer)
            {
                await _preMergeProducer!.Start();
            }
        }

        public async Task StopAsync()
        {
            await _eth2BlockProducer.StopAsync();
            if (_poSSwitcher.HasEverBeenInPos() && HasPreMergeProducer)
                await _preMergeProducer!.StopAsync();
        }

        public bool IsProducingBlocks(ulong? maxProducingInterval)
        {
            return _poSSwitcher.HasEverBeenInPos() || HasPreMergeProducer == false
                ? _eth2BlockProducer.IsProducingBlocks(maxProducingInterval)
                : _preMergeProducer!.IsProducingBlocks(maxProducingInterval);
        }

        public event EventHandler<BlockEventArgs>? BlockProduced;
    }
    
    public class Eth2BlockProducer : BlockProducerBase
    {
        public Eth2BlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            IBlockTree blockTree,
            IBlockProductionTrigger blockProductionTrigger,
            IStateProvider stateProvider,
            IGasLimitCalculator gasLimitCalculator,
            ISealEngine sealEngine,
            ITimestamper timestamper,
            ISpecProvider specProvider,
            ILogManager logManager) 
            : base(
                txSource, 
                processor, 
                sealEngine, 
                blockTree, 
                blockProductionTrigger, 
                stateProvider, 
                gasLimitCalculator, 
                timestamper, 
                specProvider, 
                logManager,
                ConstantDifficulty.Zero)
        {
        }

        protected override Block PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            Block block = base.PrepareBlock(parent, payloadAttributes);
            
            // TODO: this seems to me that it should be done in the Eth2 seal engine?
            block.Header.MixHash = Keccak.Zero;
            block.Header.ExtraData = Array.Empty<byte>();
            return block;
        }
    }
}
