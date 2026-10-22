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
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism;

public class OptimismPostMergeBlockProducer(
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
    IBlocksConfig? blocksConfig) : PostMergeBlockProducer(
    payloadAttrsTxSource.Then(txPoolTxSource),
    processor,
    blockTree,
    stateProvider,
    gasLimitCalculator,
    sealEngine,
    timestamper,
    specProvider,
    logManager,
    blocksConfig)
{
    private readonly ITxSource _payloadAttrsTxSource = payloadAttrsTxSource;
    private readonly IOptimismSpecHelper _specHelper = specHelper;

    protected override BlockToProduce PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null, IBlockProducer.Flags flags = IBlockProducer.Flags.None)
    {
        OptimismPayloadAttributes attrs = (payloadAttributes as OptimismPayloadAttributes)
            ?? throw new InvalidOperationException("Payload attributes are not set");

        BlockToProduce blockToProduce = base.PrepareBlock(parent, payloadAttributes, flags);
        if ((flags & IBlockProducer.Flags.EmptyBlock) != 0)
        {
            blockToProduce.Transactions = _payloadAttrsTxSource.GetTransactions(parent, blockToProduce.Header, attrs.GasLimit, attrs);
        }
        return blockToProduce;
    }

    protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
    {
        BlockHeader blockHeader = base.PrepareBlockHeader(parent, payloadAttributes);

        blockHeader.ExtraData = HoloceneExtraData(blockHeader, payloadAttributes);
        blockHeader.RequestsHash = _specHelper.IsIsthmus(blockHeader) ? PostIsthmusRequestHash : null;

        return blockHeader;
    }

    /// <summary>
    /// Returns the Holocene EIP-1559 parameters encoded as header extra data, or empty before Holocene.
    /// </summary>
    /// <remarks>
    /// Applied to every built block, including improved ones, because <c>OptimismHeaderValidator</c> rejects
    /// a post-Holocene header with empty extra data.
    /// </remarks>
    private byte[] HoloceneExtraData(BlockHeader blockHeader, PayloadAttributes? payloadAttributes)
    {
        if (payloadAttributes is not OptimismPayloadAttributes attrs || !_specHelper.IsHolocene(blockHeader))
        {
            return [];
        }

        // NOTE: This operation should never fail since attributes should be valid at this point.
        if (!attrs.TryDecodeEIP1559Parameters(out EIP1559Parameters eip1559Parameters, out string? error))
        {
            throw new InvalidOperationException($"{nameof(PayloadAttributes)} were not properly validated: {error}");
        }

        if (eip1559Parameters is { Denominator: 0, Elasticity: 0 })
        {
            IReleaseSpec spec = _specProvider.GetSpec(blockHeader);
            eip1559Parameters = new EIP1559Parameters(eip1559Parameters.Version, (uint)spec.BaseFeeMaxChangeDenominator, (uint)spec.ElasticityMultiplier, eip1559Parameters.MinBaseFee);
        }

        byte[] extraData = new byte[eip1559Parameters.ByteLength];
        eip1559Parameters.WriteTo(extraData);
        return extraData;
    }

    /// <summary>
    /// https://github.com/ethereum-optimism/specs/blob/main/specs/protocol/isthmus/exec-engine.md#header-validity-rules
    /// </summary>
    public static readonly Hash256 PostIsthmusRequestHash =
        new("0xe3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
}
