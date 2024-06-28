// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Optimism.Rpc;
using Nethermind.State;

namespace Nethermind.Optimism;

public class OptimismPostMergeBlockProducer : PostMergeBlockProducer
{
    private readonly ITxSource _payloadAttrsTxSource;

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
        ILogManager logManager,
        IBlocksConfig? miningConfig
    ) : base(
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
    }

    protected override Block CreateEmptyBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
    {
        OptimismPayloadAttributes attrs = (payloadAttributes as OptimismPayloadAttributes)
            ?? throw new InvalidOperationException("Payload attributes are not set");

        BlockHeader blockHeader = base.PrepareBlockHeader(parent, attrs);

        IEnumerable<Transaction> txs = _payloadAttrsTxSource.GetTransactions(parent, attrs.GasLimit, attrs);

        return new(blockHeader, txs, Array.Empty<BlockHeader>(), payloadAttributes?.Withdrawals);
    }

    protected override void AmendHeader(BlockHeader blockHeader, BlockHeader parent, PayloadAttributes? payloadAttributes = null)
    {
        base.AmendHeader(blockHeader, parent);

        blockHeader.ExtraData = Array.Empty<byte>();
    }
}
