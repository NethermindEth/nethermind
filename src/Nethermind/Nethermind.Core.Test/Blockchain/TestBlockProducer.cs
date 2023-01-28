// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
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
            IBlocksConfig blocksConfig)
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
                blocksConfig)
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
