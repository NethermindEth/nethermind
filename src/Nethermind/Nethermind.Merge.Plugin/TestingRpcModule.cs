// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Merge.Plugin;

public class TestingRpcModule(
    IMainProcessingContext mainProcessingContext,
    IGasLimitCalculator gasLimitCalculator,
    ISpecProvider specProvider,
    IBlockFinder blockFinder,
    IBlockTree blockTree,
    ILogManager logManager)
    : ITestingRpcModule
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly IBlockchainProcessor _processor = mainProcessingContext.BlockchainProcessor;
    private readonly IBlockTree _blockTree = blockTree;

    public Task<ResultWrapper<GetPayloadV5Result?>> testing_buildBlockV1(Hash256 parentBlockHash, PayloadAttributes payloadAttributes, IEnumerable<byte[]> txRlps, byte[]? extraData)
    {
        Block? parentBlock = blockFinder.FindBlock(parentBlockHash);

        if (parentBlock is not null)
        {
            BlockHeader header = PrepareBlockHeader(parentBlock.Header, payloadAttributes, extraData);
            Transaction[] transactions = GetTransactions(txRlps).ToArray();
            header.TxRoot = TxTrie.CalculateRoot(transactions);
            Block block = new(header, transactions, Array.Empty<BlockHeader>(), payloadAttributes.Withdrawals);

            FeesTracer feesTracer = new();
            Block? processedBlock = _processor.Process(block, ProcessingOptions.ProducingBlock, feesTracer);

            if (processedBlock is not null)
            {
                GetPayloadV5Result getPayloadV5Result = new(processedBlock, feesTracer.Fees, new BlobsBundleV2(processedBlock), processedBlock.ExecutionRequests!, shouldOverrideBuilder: false);

                if (!getPayloadV5Result.ValidateFork(specProvider))
                {
                    if (_logger.IsWarn) _logger.Warn($"The payload is not supported by the current fork");
                    return ResultWrapper<GetPayloadV5Result?>.Fail("unsupported fork", MergeErrorCodes.UnsupportedFork);
                }

                if (_logger.IsDebug) _logger.Debug($"testing_buildBlockV1 produced payload for block {processedBlock.Header.ToString(BlockHeader.Format.Short)}.");
                return ResultWrapper<GetPayloadV5Result?>.Success(getPayloadV5Result);
            }

            return ResultWrapper<GetPayloadV5Result?>.Fail("payload processing failed", MergeErrorCodes.UnknownPayload);
        }
        return ResultWrapper<GetPayloadV5Result?>.Fail("unknown parent block", MergeErrorCodes.InvalidPayloadAttributes);
    }

        public async Task<ResultWrapper<Hash256?>> testing_commitBlockV1(PayloadAttributes payloadAttributes, IEnumerable<byte[]> txRlps, byte[]? extraData)
    {
        BlockHeader? chainHead = blockFinder.Head?.Header;

        if (chainHead is null)
        {
            return ResultWrapper<Hash256?>.Fail("chain head not found", MergeErrorCodes.InvalidPayloadAttributes);
        }

        BlockHeader header = PrepareBlockHeader(chainHead, payloadAttributes, extraData);
        Transaction[] transactions = GetTransactions(txRlps).ToArray();
        header.TxRoot = TxTrie.CalculateRoot(transactions);
        Block block = new(header, transactions, Array.Empty<BlockHeader>(), payloadAttributes.Withdrawals);

        FeesTracer feesTracer = new();
        Block? processedBlock = _processor.Process(block, ProcessingOptions.ProducingBlock, feesTracer);

        if (processedBlock is null)
        {
            return ResultWrapper<Hash256?>.Fail("payload processing failed", MergeErrorCodes.UnknownPayload);
        }

        GetPayloadV5Result getPayloadV5Result = new(processedBlock, feesTracer.Fees, new BlobsBundleV2(processedBlock), processedBlock.ExecutionRequests!, shouldOverrideBuilder: false);

        if (!getPayloadV5Result.ValidateFork(specProvider))
        {
            if (_logger.IsWarn) _logger.Warn($"The payload is not supported by the current fork");
            return ResultWrapper<Hash256?>.Fail("unsupported fork", MergeErrorCodes.UnsupportedFork);
        }

        AddBlockResult addBlockResult = await _blockTree.SuggestBlockAsync(processedBlock, BlockTreeSuggestOptions.ShouldProcess);

        if (addBlockResult != AddBlockResult.Added)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to commit block: {addBlockResult}");
            return ResultWrapper<Hash256?>.Fail($"failed to commit block: {addBlockResult}", MergeErrorCodes.UnknownPayload);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string fileName = $"{processedBlock.Number}.json";
                string jsonContent = JsonSerializer.Serialize(getPayloadV5Result);
                await File.WriteAllTextAsync(fileName, jsonContent);
                if (_logger.IsDebug) _logger.Debug($"Saved payload to {fileName}");
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Failed to save payload to file", ex);
            }
        });

        if (_logger.IsDebug) _logger.Debug($"testing_commitBlockV1 committed block {processedBlock.Header.ToString(BlockHeader.Format.Short)} with hash {processedBlock.Hash}");

        return ResultWrapper<Hash256?>.Success(processedBlock.Hash);
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

        UInt256 difficulty = UInt256.Zero;
        header.Difficulty = difficulty;
        header.TotalDifficulty = parent.TotalDifficulty + difficulty;

        header.IsPostMerge = true;
        IReleaseSpec spec = specProvider.GetSpec(header);
        header.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, spec);

        if (spec.IsEip4844Enabled)
        {
            header.BlobGasUsed = 0;
            header.ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
        }

        if (spec.WithdrawalsEnabled)
        {
            header.WithdrawalsRoot = payloadAttributes.Withdrawals is null || payloadAttributes.Withdrawals.Length == 0
                ? Keccak.EmptyTreeHash
                : new WithdrawalTrie(payloadAttributes.Withdrawals).RootHash;
        }

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
