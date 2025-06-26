// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Optimism.Rpc;
using Nethermind.State;

namespace Nethermind.Optimism;

public class OptimismPostMergeBlockProducer : PostMergeBlockProducer
{
    private readonly ITxSource _payloadAttrsTxSource;
    private readonly IOptimismSpecHelper _specHelper;

    public OptimismPostMergeBlockProducer(
        ITxSource payloadAttrsTxSource,
        ITxSource txPoolTxSource,
        IBlockchainProcessor processor,
        IBlockTree blockTree,
        IWorldState stateProvider,
        IGasLimitCalculator gasLimitCalculator,
        ISealEngine sealEngine,
        ITimestamper timestamper,
        ISpecProvider specProvider,
        IOptimismSpecHelper specHelper,
        ILogManager logManager,
        IBlocksConfig? miningConfig) : base(
        payloadAttrsTxSource.Then(txPoolTxSource),
        processor,
        blockTree,
        stateProvider,
        gasLimitCalculator,
        sealEngine,
        timestamper,
        specProvider,
        logManager,
        miningConfig)
    {
        _payloadAttrsTxSource = payloadAttrsTxSource;
        _specHelper = specHelper;
    }

    protected override BlockToProduce PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null, IBlockProducer.Flags flags = (IBlockProducer.Flags)0)
    {
        OptimismPayloadAttributes attrs = (payloadAttributes as OptimismPayloadAttributes)
                                          ?? throw new InvalidOperationException("Payload attributes are not set");

        BlockToProduce blockToProduce = base.PrepareBlock(parent, payloadAttributes, flags);
        if ((flags & IBlockProducer.Flags.EmptyBlock) != 0)
        {
            blockToProduce.Transactions = _payloadAttrsTxSource.GetTransactions(parent, attrs.GasLimit, attrs);
        }
        return blockToProduce;
    }

    protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
    {
        BlockHeader blockHeader = base.PrepareBlockHeader(parent, payloadAttributes);

        blockHeader.ExtraData = [];
        blockHeader.RequestsHash = _specHelper.IsIsthmus(blockHeader) ? PostIsthmusRequestHash : null;

        return blockHeader;
    }

    /// <summary>
    /// https://github.com/ethereum-optimism/specs/blob/main/specs/protocol/isthmus/exec-engine.md#header-validity-rules
    /// </summary>
    public static readonly Hash256 PostIsthmusRequestHash =
        new("0xe3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
}
