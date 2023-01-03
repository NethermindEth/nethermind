// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.BlockProduction
{
    public class PostMergeBlockProducer : BlockProducerBase
    {
        public PostMergeBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            IBlockTree blockTree,
            IBlockProductionTrigger blockProductionTrigger,
            IStateProvider stateProvider,
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
                blockProductionTrigger,
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

        public Block PrepareEmptyBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            BlockHeader blockHeader = PrepareBlockHeader(parent, payloadAttributes);
            blockHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            blockHeader.TxRoot = Keccak.EmptyTreeHash;
            blockHeader.Bloom = Bloom.Empty;

            Block block = new(blockHeader, Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), payloadAttributes?.Withdrawals);

            // processing is only done here to apply block rewards in AuRa
            if (TrySetState(parent.StateRoot))
            {
                block = ProcessPreparedBlock(block, null) ?? block;
            }

            return block;
        }

        protected override Block PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            Block block = base.PrepareBlock(parent, payloadAttributes);
            AmendHeader(block.Header);

            IReleaseSpec spec = _specProvider.GetSpec(block.Number, block.Timestamp);
            if (spec.IsEip4844Enabled)
            {
                block.Header.ExcessDataGas = IntrinsicGasCalculator.CalculateExcessDataGas(parent.ExcessDataGas,
                    block.Transactions.Sum(x => x.BlobVersionedHashes?.Length ?? 0), spec);
                block.Header.ParentExcessDataGas = parent.ExcessDataGas ?? 0;
            }

            return block;
        }

        protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            BlockHeader blockHeader = base.PrepareBlockHeader(parent, payloadAttributes);
            AmendHeader(blockHeader);
            return blockHeader;
        }

        // TODO: this seems to me that it should be done in the Eth2 seal engine?
        private void AmendHeader(BlockHeader blockHeader)
        {
            blockHeader.ExtraData = _blocksConfig.GetExtraDataBytes();
            blockHeader.IsPostMerge = true;
        }
    }
}
