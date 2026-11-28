// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
        /// <remarks>Every prepared block records the inclusion list, so the entries still missing after the main
        /// transaction loop can be offered again. The fallback payload still has to satisfy the list, so the empty
        /// block also takes its transactions from it even though it skips mempool selection (EIP-7805).</remarks>
        protected override BlockToProduce PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null, IBlockProducer.Flags flags = IBlockProducer.Flags.None)
        {
            BlockToProduce blockToProduce = base.PrepareBlock(parent, payloadAttributes, flags);
            if (inclusionListTxSource is not null)
            {
                IEnumerable<MempoolTransaction> inclusionList = inclusionListTxSource.GetTransactions(parent, blockToProduce.Header, blockToProduce.Header.GasLimit, payloadAttributes);
                blockToProduce.InclusionList = Array.ConvertAll(inclusionList as MempoolTransaction[] ?? [.. inclusionList], static tx => tx.Transaction);
                if ((flags & IBlockProducer.Flags.EmptyBlock) != 0)
                {
                    blockToProduce.Transactions = inclusionList;
                }
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
