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
        IBlocksConfig? blocksConfig)
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
        protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            BlockHeader blockHeader = base.PrepareBlockHeader(parent, payloadAttributes);

            blockHeader.ExtraData = _blocksConfig.GetExtraDataBytes();
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
