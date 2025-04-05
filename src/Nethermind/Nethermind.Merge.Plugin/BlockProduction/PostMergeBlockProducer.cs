// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Evm;

namespace Nethermind.Merge.Plugin.BlockProduction
{
    public class PostMergeBlockProducer : BlockProducerBase
    {
        public PostMergeBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            IBlockTree blockTree,
            IWorldState stateProvider,
            IGasLimitCalculator gasLimitCalculator,
            ISealEngine sealEngine,
            ITimestamper timestamper,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlocksConfig? miningConfig)
            : base(
                txSource,
                processor,
                sealEngine,
                blockTree,
                stateProvider,
                gasLimitCalculator,
                timestamper,
                specProvider,
                logManager,
                ConstantDifficulty.Zero,
                miningConfig
            )
        {
        }

        public bool SupportsBlobs => TxSource.SupportsBlobs;

        public virtual Block PrepareEmptyBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            Block block = CreateEmptyBlock(parent, payloadAttributes);

            if (_producingBlockLock.Wait(BlockProductionTimeoutMs))
            {
                try
                {
                    if (TrySetState(parent.StateRoot))
                    {
                        return ProcessPreparedBlock(block, null) ?? throw new EmptyBlockProductionException("Block processing failed");
                    }
                }
                finally
                {
                    _producingBlockLock.Release();
                }
            }

            throw new EmptyBlockProductionException("Setting state for processing block failed");
        }

        protected virtual Block CreateEmptyBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            BlockHeader blockHeader = PrepareBlockHeader(parent, payloadAttributes);
            blockHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            blockHeader.TxRoot = Keccak.EmptyTreeHash;
            blockHeader.Bloom = Bloom.Empty;
            return new Block(blockHeader, Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), payloadAttributes?.Withdrawals);
        }

        protected override Block PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            Block block = base.PrepareBlock(parent, payloadAttributes);
            AmendHeader(block.Header, parent);
            return block;
        }

        protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            BlockHeader blockHeader = base.PrepareBlockHeader(parent, payloadAttributes);
            AmendHeader(blockHeader, parent, payloadAttributes);
            return blockHeader;
        }

        // TODO: this seems to me that it should be done in the Eth2 seal engine?
        protected virtual void AmendHeader(BlockHeader blockHeader, BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            blockHeader.ExtraData = _blocksConfig.GetExtraDataBytes();
            blockHeader.IsPostMerge = true;
            IReleaseSpec spec = _specProvider.GetSpec(blockHeader);

            if (spec.IsEip4844Enabled)
            {
                blockHeader.BlobGasUsed = 0;
                blockHeader.ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
            }
        }
    }
}
