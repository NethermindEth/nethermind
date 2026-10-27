// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Evm.State;

namespace Nethermind.Core.Test.Blockchain
{
    public class TestBlockProducer(
        ITxSource txSource,
        IBlockchainProcessor processor,
        IWorldState stateProvider,
        ISealer sealer,
        IBlockTree blockTree,
        ITimestamper timestamper,
        ISpecProvider specProvider,
        ILogManager logManager,
        IBlocksConfig blocksConfig) : BlockProducerBase(
            txSource,
            processor,
            sealer,
            blockTree,
            stateProvider,
            new FollowOtherMiners(specProvider),
            timestamper,
            specProvider,
            logManager,
            ConstantDifficulty.One,
            blocksConfig)
    {
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

        /// <summary>Whether produced headers carry the EIP-4844 blob fields, as a post-merge producer's do, so blob-carrying
        /// transactions can be included.</summary>
        public bool PrepareBlobFields { get; set; }

        protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            BlockHeader header = base.PrepareBlockHeader(parent, payloadAttributes);
            IReleaseSpec spec = _specProvider.GetSpec(header);
            if (PrepareBlobFields && spec.IsEip4844Enabled)
            {
                header.BlobGasUsed = 0;
                header.ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
            }

            return header;
        }

        protected override Task<Block?> TryProduceNewBlock(CancellationToken token, BlockHeader? parentHeader, IBlockTracer? blockTracer = null, PayloadAttributes? payloadAttributes = null, IBlockProducer.Flags flags = IBlockProducer.Flags.None)
        {
            parentHeader ??= BlockParent;
            return base.TryProduceNewBlock(token, parentHeader, blockTracer, payloadAttributes, flags);
        }
    }
}
