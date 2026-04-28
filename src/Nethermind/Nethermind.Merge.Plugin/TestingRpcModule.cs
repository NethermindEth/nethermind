// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
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
    IBlockProducerEnvFactory blockProducerEnvFactory,
    IGasLimitCalculator gasLimitCalculator,
    ISpecProvider specProvider,
    IBlockFinder blockFinder,
    ILogManager logManager)
    : ITestingRpcModule
{
    private readonly ILogger _logger = logManager.GetClassLogger<TestingRpcModule>();

    public async Task<ResultWrapper<object?>> testing_buildBlockV1(Hash256 parentBlockHash, PayloadAttributes payloadAttributes, IEnumerable<byte[]>? txRlps, byte[]? extraData = null)
    {
        Block? parentBlock = blockFinder.FindBlock(parentBlockHash);

        if (parentBlock is not null)
        {
            IReleaseSpec spec = specProvider.GetSpec(new ForkActivation(parentBlock.Header.Number + 1, payloadAttributes.Timestamp));

            BlockHeader header = PrepareBlockHeader(parentBlock.Header, payloadAttributes, spec, extraData);

            // Create a fresh processor per call with its own WorldState to avoid scope conflicts
            // with the main processing pipeline (TrieWarmer/prewarmer may hold scopes open).
            await using ScopedBlockProducerEnv env = blockProducerEnvFactory.CreateTransient();

            Transaction[] transactions;
            try
            {
                transactions = (txRlps is null
                        ? env.TxSource.GetTransactions(parentBlock.Header, header.GasLimit, payloadAttributes, filterSource: true)
                        : DecodeTransactions(txRlps))
                    .ToArray();
            }
            catch (RlpException e)
            {
                return ResultWrapper<object?>.Fail($"invalid transaction RLP: {e.Message}", ErrorCodes.InvalidInput);
            }

            header.TxRoot = TxTrie.CalculateRoot(transactions);
            BlockToProduce block = new(header, transactions, [], spec.WithdrawalsEnabled ? (payloadAttributes.Withdrawals ?? []) : null);

            FeesTracer feesTracer = new();
            Block? processedBlock = env.ChainProcessor.Process(block, ProcessingOptions.ProducingBlock, feesTracer);

            if (processedBlock is not null)
            {
                // When explicit transactions were provided, verify all were included.
                // The block processor in production mode silently skips invalid transactions,
                // but the spec requires all provided transactions to be included.
                if (txRlps is not null && processedBlock.Transactions.Length != transactions.Length)
                {
                    string error = $"expected {transactions.Length} transactions but only {processedBlock.Transactions.Length} were included";
                    if (_logger.IsWarn) _logger.Warn($"testing_buildBlockV1 failed: {error}");
                    return ResultWrapper<object?>.Fail(error, ErrorCodes.InvalidInput);
                }

                object getPayloadResult = CreateGetPayloadResult(processedBlock, feesTracer.Fees, spec);

                if (_logger.IsDebug) _logger.Debug($"testing_buildBlockV1 produced payload for block {processedBlock.Header.ToString(BlockHeader.Format.Short)}.");
                return ResultWrapper<object?>.Success(getPayloadResult);
            }

            return ResultWrapper<object?>.Fail("payload processing failed", ErrorCodes.InternalError);
        }

        return ResultWrapper<object?>.Fail("unknown parent block", MergeErrorCodes.InvalidPayloadAttributes);
    }

    private BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes payloadAttributes, IReleaseSpec spec, byte[]? extraData)
    {
        Address blockAuthor = payloadAttributes.SuggestedFeeRecipient ?? Address.Zero;
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

    private static IEnumerable<Transaction> DecodeTransactions(IEnumerable<byte[]> txRlps) =>
        txRlps.Select(txRlp => Rlp.Decode<Transaction>(txRlp, RlpBehaviors.SkipTypedWrapping));

    private static object CreateGetPayloadResult(Block processedBlock, UInt256 blockFees, IReleaseSpec spec)
    {
        processedBlock.ExecutionRequests ??= ExecutionRequestExtensions.EmptyRequests;
        processedBlock.Header.RequestsHash ??= ExecutionRequestExtensions.EmptyRequestsHash;

        return spec.IsEip7928Enabled
            ? new GetPayloadV6Result(processedBlock, blockFees, new BlobsBundleV2(processedBlock), processedBlock.ExecutionRequests, shouldOverrideBuilder: false)
            : new GetPayloadV5Result(processedBlock, blockFees, new BlobsBundleV2(processedBlock), processedBlock.ExecutionRequests, shouldOverrideBuilder: false);
    }

}
