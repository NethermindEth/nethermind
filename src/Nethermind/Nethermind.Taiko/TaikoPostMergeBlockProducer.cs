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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoPostMergeBlockProducer(
    IBlockchainProcessor processor,
    IBlockTree blockTree,
    IWorldState stateProvider,
    IGasLimitCalculator gasLimitCalculator,
    ISealEngine sealEngine,
    ITimestamper timestamper,
    ISpecProvider specProvider,
    ILogManager logManager,
    IBlocksConfig? miningConfig
    ) : PostMergeBlockProducer(
    EmptyTxSource.Instance,
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
    private readonly TaikoPayloadTxSource _payloadAttrsTxSource = new();

    protected override Block CreateEmptyBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
    {
        TaikoPayloadAttributes attrs = (payloadAttributes as TaikoPayloadAttributes)
            ?? throw new InvalidOperationException("Payload attributes are not set");

        BlockHeader blockHeader = PrepareBlockHeader(parent, attrs);

        IEnumerable<Transaction> txs = _payloadAttrsTxSource.GetTransactions(parent, attrs.BlockMetadata!.GasLimit, attrs);

        return new(blockHeader, txs, Array.Empty<BlockHeader>(), payloadAttributes?.Withdrawals);
    }

    protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
    {
        TaikoPayloadAttributes attrs = (payloadAttributes as TaikoPayloadAttributes)
            ?? throw new InvalidOperationException("Payload attributes have incorrect type. Expected TaikoPayloadAttributes.");

        BlockHeader header = new(
            parent.Hash!,
            Keccak.OfAnEmptySequenceRlp,
            attrs.BlockMetadata!.Beneficiary!,
            UInt256.Zero,
            parent.Number + 1,
            attrs.BlockMetadata.GasLimit,
            attrs.Timestamp,
            attrs.BlockMetadata.ExtraData!)
        {
            MixHash = attrs.BlockMetadata.MixHash,
            ParentBeaconBlockRoot = attrs.ParentBeaconBlockRoot,
            BaseFeePerGas = attrs.BaseFeePerGas,
            Difficulty = UInt256.Zero,
            TotalDifficulty = UInt256.Zero
        };

        AmendHeader(header, parent, payloadAttributes);

        return header;
    }

    protected override void AmendHeader(BlockHeader blockHeader, BlockHeader parent, PayloadAttributes? payloadAttributes = null)
    {
        base.AmendHeader(blockHeader, parent);

        TaikoPayloadAttributes attrs = (payloadAttributes as TaikoPayloadAttributes)
            ?? throw new InvalidOperationException("Payload attributes are not set");

        blockHeader.ExtraData = attrs.BlockMetadata!.ExtraData!;
        blockHeader.Beneficiary = attrs.BlockMetadata!.Beneficiary!;
        blockHeader.GasLimit = attrs.BlockMetadata!.GasLimit!;
        blockHeader.MixHash = attrs.BlockMetadata!.MixHash!;
        blockHeader.BaseFeePerGas = attrs.BaseFeePerGas;

        blockHeader.Author = blockHeader.Beneficiary;
        blockHeader.Difficulty = UInt256.Zero;
        blockHeader.TotalDifficulty = UInt256.Zero;
    }
}
