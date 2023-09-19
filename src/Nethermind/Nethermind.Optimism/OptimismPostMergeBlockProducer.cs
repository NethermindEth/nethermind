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
        IBlockProductionTrigger blockProductionTrigger,
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
        blockProductionTrigger,
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

    public override Block PrepareEmptyBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
    {
        OptimismPayloadAttributes attrs = (payloadAttributes as OptimismPayloadAttributes)
            ?? throw new InvalidOperationException("Payload attributes are not set");

        BlockHeader blockHeader = base.PrepareBlockHeader(parent, attrs);

        IEnumerable<Transaction> txs = _payloadAttrsTxSource.GetTransactions(parent, attrs.GasLimit, attrs);

        Block block = new(blockHeader, txs, Array.Empty<BlockHeader>(), payloadAttributes?.Withdrawals);

        if (_producingBlockLock.Wait(BlockProductionTimeout))
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

    protected override void AmendHeader(BlockHeader blockHeader, BlockHeader parent)
    {
        base.AmendHeader(blockHeader, parent);

        blockHeader.ExtraData = Array.Empty<byte>();
    }
}
