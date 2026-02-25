// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    ILogManager logManager)
    : ITestingRpcModule
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly IBlockchainProcessor _processor = mainProcessingContext.BlockchainProcessor;

    public Task<ResultWrapper<object?>> testing_buildBlockV1(Hash256 parentBlockHash, PayloadAttributes payloadAttributes, IEnumerable<byte[]> txRlps, byte[]? extraData = null, string? targetFork = null)
    {
        Block? parentBlock = blockFinder.FindBlock(parentBlockHash);
        if (parentBlock is null)
        {
            return ResultWrapper<object?>.Fail("unknown parent block", MergeErrorCodes.InvalidPayloadAttributes);
        }

        if (!TryResolveTargetFork(targetFork, out TargetFork resolvedFork))
        {
            if (_logger.IsWarn) _logger.Warn($"The payload is not supported by the target fork: {targetFork ?? "default"}");
            return ResultWrapper<object?>.Fail("unsupported fork", MergeErrorCodes.UnsupportedFork);
        }

        if (!ValidatePayloadAttributes(payloadAttributes, resolvedFork, out ResultWrapper<object?>? errorResult))
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid payload attributes: {errorResult!.Result.Error}");
            return errorResult!;
        }

        IReleaseSpec spec = specProvider.GetSpec(new ForkActivation(parentBlock.Header.Number + 1, payloadAttributes.Timestamp));
        BlockHeader header = PrepareBlockHeader(parentBlock.Header, payloadAttributes, extraData);
        Transaction[] transactions = GetTransactions(txRlps).ToArray();
        header.TxRoot = TxTrie.CalculateRoot(transactions);
        Block block = new(header, transactions, Array.Empty<BlockHeader>(), payloadAttributes.Withdrawals);

        FeesTracer feesTracer = new();
        Block? processedBlock = _processor.Process(block, ProcessingOptions.ProducingBlock, feesTracer);

        if (processedBlock is null)
        {
            return ResultWrapper<object?>.Fail("payload processing failed", MergeErrorCodes.UnknownPayload);
        }

        object getPayloadResult = CreateGetPayloadResult(processedBlock, feesTracer.Fees, resolvedFork);

        if (_logger.IsDebug) _logger.Debug($"testing_buildBlockV1 produced payload for block {processedBlock.Header.ToString(BlockHeader.Format.Short)}.");
        return ResultWrapper<object?>.Success(getPayloadResult);
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
            ParentBeaconBlockRoot = payloadAttributes.ParentBeaconBlockRoot,
            SlotNumber = payloadAttributes.SlotNumber
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

    private object CreateGetPayloadResult(Block processedBlock, UInt256 blockFees, TargetFork resolvedFork) =>
        resolvedFork == TargetFork.Amsterdam
            ? new GetPayloadV6Result(processedBlock, blockFees, new BlobsBundleV2(processedBlock), processedBlock.ExecutionRequests!, shouldOverrideBuilder: false)
            : new GetPayloadV5Result(processedBlock, blockFees, new BlobsBundleV2(processedBlock), processedBlock.ExecutionRequests!, shouldOverrideBuilder: false);

    private bool ValidatePayloadAttributes(PayloadAttributes payloadAttributes, TargetFork resolvedFork, out ResultWrapper<object?>? errorResult)
    {
        if (resolvedFork == TargetFork.Amsterdam)
        {
            if (payloadAttributes.SlotNumber is null)
            {
                errorResult = ResultWrapper<object?>.Fail("payload attributes missing slotNumber", MergeErrorCodes.InvalidPayloadAttributes);
                return false;
            }
        }
        else if (payloadAttributes.SlotNumber is not null)
        {
            errorResult = ResultWrapper<object?>.Fail("slotNumber is not supported before Amsterdam", MergeErrorCodes.InvalidPayloadAttributes);
            return false;
        }

        errorResult = null;
        return true;
    }

    private static bool TryResolveTargetFork(string? targetFork, out TargetFork resolvedFork)
    {
        if (string.IsNullOrWhiteSpace(targetFork))
        {
            resolvedFork = TargetFork.Prague;
            return true;
        }

        resolvedFork = targetFork.Trim().ToLowerInvariant() switch
        {
            "amsterdam" => TargetFork.Amsterdam,
            "glamsterdam" => TargetFork.Amsterdam,
            "prague" => TargetFork.Prague,
            "pectra" => TargetFork.Prague,
            _ => TargetFork.Unknown
        };

        return resolvedFork != TargetFork.Unknown;
    }

    private enum TargetFork
    {
        Unknown = 0,
        Prague = 1,
        Amsterdam = 2
    }
}
