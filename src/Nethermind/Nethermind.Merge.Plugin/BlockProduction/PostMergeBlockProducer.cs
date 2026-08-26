// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Evm;
using Nethermind.Evm.State;

namespace Nethermind.Merge.Plugin.BlockProduction
{
    public class PostMergeBlockProducer(
        ITxSource txSource,
        IBlockchainProcessor processor,
        IBlockTree blockTree,
        IWorldState stateProvider,
        IGasLimitCalculator gasLimitCalculator,
        ISealEngine sealEngine,
        ITimestamper timestamper,
        ISpecProvider specProvider,
        ILogManager logManager,
        IBlocksConfig? blocksConfig,
        IInclusionListTxSource? inclusionListTxSource = null)
        : BlockProducerBase(txSource,
            processor,
            sealEngine,
            blockTree,
            stateProvider,
            gasLimitCalculator,
            timestamper,
            specProvider,
            logManager,
            ConstantDifficulty.Zero,
            blocksConfig)
    {
        /// <remarks>The fallback payload still has to satisfy the inclusion list, so the empty block carries
        /// the list even though it skips mempool selection (EIP-7805).</remarks>
        protected override BlockToProduce PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null, IBlockProducer.Flags flags = IBlockProducer.Flags.None)
        {
            BlockToProduce blockToProduce = base.PrepareBlock(parent, payloadAttributes, flags);
            if (inclusionListTxSource is not null && (flags & IBlockProducer.Flags.EmptyBlock) != 0)
            {
                blockToProduce.Transactions = inclusionListTxSource.GetTransactions(parent, blockToProduce.Header.GasLimit, payloadAttributes);
            }
            return blockToProduce;
        }

        protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            BlockHeader blockHeader = base.PrepareBlockHeader(parent, payloadAttributes);

            blockHeader.IsPostMerge = true;
            IReleaseSpec spec = _specProvider.GetSpec(blockHeader);

            if (spec.IsEip4844Enabled)
            {
                blockHeader.BlobGasUsed = 0;
                blockHeader.ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
            }

            return blockHeader;
        }
    }
}
