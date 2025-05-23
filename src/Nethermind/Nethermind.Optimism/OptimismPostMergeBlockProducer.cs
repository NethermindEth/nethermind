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

    protected override Block CreateEmptyBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
    {
        OptimismPayloadAttributes attrs = (payloadAttributes as OptimismPayloadAttributes)
            ?? throw new InvalidOperationException("Payload attributes are not set");

        BlockHeader blockHeader = base.PrepareBlockHeader(parent, attrs);

        IEnumerable<Transaction> txs = _payloadAttrsTxSource.GetTransactions(parent, attrs.GasLimit, attrs);

        Block block = new(blockHeader, txs, Array.Empty<BlockHeader>(), payloadAttributes?.Withdrawals);

        if (_producingBlockLock.Wait(BlockProductionTimeoutMs))
        {
            try
            {
                if (TrySetState(parent.StateRoot))
                {
                    return ProcessPreparedBlock(block, null) ?? throw new EmptyBlockProductionException("Block processing failed");
                }
                else
                {
                    throw new EmptyBlockProductionException($"Setting state for processing block failed: couldn't set state to stateRoot {parent.StateRoot}");
                }
            }
            finally
            {
                _producingBlockLock.Release();
            }
        }

        throw new EmptyBlockProductionException("Setting state for processing block failed");
    }

    protected override void AmendHeader(BlockHeader blockHeader, BlockHeader parent, PayloadAttributes? payloadAttributes = null)
    {
        base.AmendHeader(blockHeader, parent);

        blockHeader.ExtraData = [];
        blockHeader.RequestsHash = _specHelper.IsIsthmus(blockHeader) ? PostIsthmusRequestHash : null;
    }

    /// <summary>
    /// https://github.com/ethereum-optimism/specs/blob/main/specs/protocol/isthmus/exec-engine.md#header-validity-rules
    /// </summary>
    public static readonly Hash256 PostIsthmusRequestHash =
        new("0xe3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
}
