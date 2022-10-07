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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;

namespace Nethermind.Core.Test.Blockchain
{
    public class TestBlockProducer : BlockProducerBase
    {
        public TestBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            IStateProvider stateProvider,
            ISealer sealer,
            IBlockTree blockTree,
            IBlockProductionTrigger blockProductionTrigger,
            ITimestamper timestamper,
            ISpecProvider specProvider,
            ILogManager logManager,
            IMiningConfig miningConfig)
            : base(
                txSource,
                processor,
                sealer,
                blockTree,
                blockProductionTrigger,
                stateProvider,
                new FollowOtherMiners(specProvider),
                timestamper,
                specProvider,
                logManager,
                ConstantDifficulty.One,
                miningConfig)
        {
        }

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

        protected override Task<Block?> TryProduceNewBlock(CancellationToken token, BlockHeader? parentHeader, IBlockTracer? blockTracer = null, PayloadAttributes? payloadAttributes = null)
        {
            parentHeader ??= BlockParent;
            return base.TryProduceNewBlock(token, parentHeader, blockTracer, payloadAttributes);
        }
    }
}
