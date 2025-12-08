// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Merge.Plugin;

public class TestingRpcModule(
    IBlockchainProcessor processor,
    IGasLimitCalculator gasLimitCalculator,
    IDifficultyCalculator difficultyCalculator,
    ISpecProvider specProvider,
    IBlockFinder blockFinder,
    ILogManager logManager)
    : ITestingRpcModule
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public Task<ResultWrapper<GetPayloadV5Result?>> testing_buildBlockV1(Hash256 parentBlockHash, PayloadAttributes payloadAttributes, IEnumerable<byte[]> txRlps, byte[]? extraData)
    {
        Block? parentBlock = blockFinder.FindBlock(parentBlockHash);

        if (parentBlock is not null)
        {
            BlockHeader header = PrepareBlockHeader(parentBlock.Header, payloadAttributes, extraData);
            IEnumerable<Transaction> transactions = GetTransactions(txRlps);
            Block block = new BlockToProduce(header, transactions, Array.Empty<BlockHeader>(), payloadAttributes.Withdrawals);

            FeesTracer feesTracer = new();
            Block? processedBlock = processor.Process(block, ProcessingOptions.ProducingBlock, feesTracer);

            if (processedBlock is not null)
            {
                GetPayloadV5Result getPayloadV5Result = new(processedBlock, feesTracer.Fees, new BlobsBundleV2(processedBlock), processedBlock.ExecutionRequests!, shouldOverrideBuilder: false);

                if (!getPayloadV5Result.ValidateFork(specProvider))
                {
                    if (_logger.IsWarn) _logger.Warn($"The payload is not supported by the current fork");
                    return ResultWrapper<GetPayloadV5Result?>.Fail("unsupported fork", MergeErrorCodes.UnsupportedFork);
                }

                if (_logger.IsInfo) _logger.Info($"GetPayloadV5 result: {block.Header.ToString(BlockHeader.Format.Short)}.");
                return ResultWrapper<GetPayloadV5Result?>.Success(getPayloadV5Result);
            }

            return ResultWrapper<GetPayloadV5Result?>.Fail("payload processing failed", MergeErrorCodes.UnknownPayload);
        }
        return ResultWrapper<GetPayloadV5Result?>.Fail("unknown parent block", MergeErrorCodes.InvalidPayloadAttributes);
    }

    private BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes payloadAttributes, byte[]? extraData)
    {
        Address blockAuthor = payloadAttributes.SuggestedFeeRecipient;
        BlockHeader header = new(
            parent.Hash!,
            Keccak.OfAnEmptySequenceRlp,
            blockAuthor,
            UInt256.Zero,
            parent.Number + 1,
            payloadAttributes.GetGasLimit() ?? gasLimitCalculator.GetGasLimit(parent),
            payloadAttributes.Timestamp,
            extraData ?? [])
        {
            Author = blockAuthor,
            MixHash = payloadAttributes.PrevRandao,
            ParentBeaconBlockRoot = payloadAttributes.ParentBeaconBlockRoot
        };

        UInt256 difficulty = difficultyCalculator.Calculate(header, parent);
        header.Difficulty = difficulty;
        header.TotalDifficulty = parent.TotalDifficulty + difficulty;
        header.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, specProvider.GetSpec(header));

        return header;
    }

    private IEnumerable<Transaction> GetTransactions(IEnumerable<byte[]> txRlps)
    {
        foreach (var txRlp in txRlps)
        {
            yield return TxDecoder.Instance.Decode(new RlpStream(txRlp), RlpBehaviors.SkipTypedWrapping);
        }
    }
}
