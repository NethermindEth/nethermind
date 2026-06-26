// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
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
    IMainStateBlockProducerEnvFactory mainStateBlockProducerEnvFactory,
    IGasLimitCalculator gasLimitCalculator,
    ISpecProvider specProvider,
    IBlockFinder blockFinder,
    IBlockTree blockTree,
    IProcessExitSource processExitSource,
    ILogManager logManager)
    : ITestingRpcModule, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<TestingRpcModule>();
    private readonly SemaphoreSlim _commitLock = new(1, 1);

    // The commit pass updates the head without re-processing, so it must run on the main
    // world state to persist the produced post-state. Reuse across calls is safe because
    // BranchProcessor.Process opens a fresh world-state scope on entry.
    private readonly IBlockProducerEnv _commitEnv = mainStateBlockProducerEnvFactory.CreatePersistent();

    public void Dispose() => _commitLock.Dispose();

    public async Task<ResultWrapper<object>> testing_buildBlockV1(Hash256 parentBlockHash, PayloadAttributes payloadAttributes, IEnumerable<byte[]>? txRlps, byte[]? extraData = null)
    {
        Block? parentBlock = blockFinder.FindBlock(parentBlockHash);
        if (parentBlock is null)
            return ResultWrapper<object>.Fail("unknown parent block", MergeErrorCodes.InvalidPayloadAttributes);

        FeesTracer feesTracer = new();
        await using ScopedBlockProducerEnv env = blockProducerEnvFactory.CreateTransient();
        ResultWrapper<ProducedBlock> produced = ProduceBlock(
            env, parentBlock.Header, payloadAttributes, txRlps, extraData,
            nameof(testing_buildBlockV1), processExitSource.Token,
            feesTracer, ProcessingOptions.ProducingBlock);
        if (produced.Result.ResultType == ResultType.Failure)
            return ResultWrapper<object>.Fail(produced.Result.Error!, produced.ErrorCode);

        if (_logger.IsDebug) _logger.Debug($"testing_buildBlockV1 produced payload for block {produced.Data.Block.Header.ToString(BlockHeader.Format.Short)}.");
        return ResultWrapper<object>.Success(CreateGetPayloadResult(produced.Data.Block, feesTracer.Fees, produced.Data.Spec));
    }

    public async Task<ResultWrapper<Hash256>> testing_commitBlockV1(
        PayloadAttributes payloadAttributes, IEnumerable<byte[]>? txRlps, byte[]? extraData = null)
    {
        CancellationToken exitToken = processExitSource.Token;
        try
        {
            await _commitLock.WaitAsync(exitToken);
        }
        catch (OperationCanceledException)
        {
            return ResultWrapper<Hash256>.Fail("node is shutting down", ErrorCodes.InternalError);
        }

        try
        {
            if (blockTree.Head?.Header is not BlockHeader chainHead)
                return ResultWrapper<Hash256>.Fail("chain head not found", ErrorCodes.InternalError);

            // ForceProcessing: the produced block is not yet better than the current head.
            // Persistence is decided by the env's (main) world state, not by these options.
            const ProcessingOptions ProducerOptions =
                ProcessingOptions.NoValidation
                | ProcessingOptions.ForceProcessing
                | ProcessingOptions.DoNotUpdateHead
                | ProcessingOptions.StoreReceipts;

            ResultWrapper<ProducedBlock> produced = ProduceBlock(
                _commitEnv, chainHead, payloadAttributes, txRlps, extraData,
                nameof(testing_commitBlockV1), exitToken,
                NullBlockTracer.Instance, ProducerOptions);
            if (produced.Result.ResultType == ResultType.Failure)
                return ResultWrapper<Hash256>.Fail(produced.Result.Error!, produced.ErrorCode);

            return CommitAsMainChain(produced.Data.Block);
        }
        finally
        {
            _commitLock.Release();
        }
    }

    private readonly record struct ProducedBlock(Block Block, IReleaseSpec Spec);

    private ResultWrapper<ProducedBlock> ProduceBlock(
        IBlockProducerEnv env,
        BlockHeader parent,
        PayloadAttributes payloadAttributes,
        IEnumerable<byte[]>? txRlps,
        byte[]? extraData,
        string operationName,
        CancellationToken cancellationToken,
        IBlockTracer tracer,
        ProcessingOptions options)
    {
        IReleaseSpec spec = specProvider.GetSpec(new ForkActivation(parent.Number + 1, payloadAttributes.Timestamp));
        BlockHeader header = PrepareBlockHeader(parent, payloadAttributes, spec, extraData);

        Transaction[] transactions;
        try
        {
            IEnumerable<Transaction> txs = txRlps is null
                ? env.TxSource.GetTransactions(parent, header.GasLimit, payloadAttributes, filterSource: true)
                : DecodeTransactions(txRlps);

            transactions = txs.ToArray();
        }
        catch (RlpException e)
        {
            return ResultWrapper<ProducedBlock>.Fail($"invalid transaction RLP: {e.Message}", ErrorCodes.InvalidInput);
        }

        header.TxRoot = TxTrie.CalculateRoot(transactions);
        BlockToProduce block = new(header, transactions, [], spec.WithdrawalsEnabled ? (payloadAttributes.Withdrawals ?? []) : null);

        Block? processedBlock = env.ChainProcessor.Process(block, options, tracer, cancellationToken);

        if (processedBlock is null)
            return cancellationToken.IsCancellationRequested
                ? ResultWrapper<ProducedBlock>.Fail("node is shutting down", ErrorCodes.InternalError)
                : ResultWrapper<ProducedBlock>.Fail("payload processing failed", ErrorCodes.InternalError);

        if (txRlps is not null && processedBlock.Transactions.Length != transactions.Length)
        {
            string error = $"expected {transactions.Length} transactions but only {processedBlock.Transactions.Length} were included";
            if (_logger.IsWarn) _logger.Warn($"{operationName} failed: {error}");
            return ResultWrapper<ProducedBlock>.Fail(error, ErrorCodes.InvalidInput);
        }

        return ResultWrapper<ProducedBlock>.Success(new ProducedBlock(processedBlock, spec));
    }

    /// <summary>
    /// Advance the canonical head to an already-processed block via
    /// <see cref="IBlockTree.TryUpdateMainChain"/>, bypassing the main BlockchainProcessor.
    /// </summary>
    private ResultWrapper<Hash256> CommitAsMainChain(Block processedBlock)
    {
        if (processedBlock.Hash is null)
            return ResultWrapper<Hash256>.Fail("processed block has no hash", ErrorCodes.InternalError);

        AddBlockResult addBlockResult = blockTree.SuggestBlock(processedBlock, BlockTreeSuggestOptions.ForceDontSetAsMain);
        if (addBlockResult != AddBlockResult.Added)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to commit block: {addBlockResult}");
            return ResultWrapper<Hash256>.Fail($"failed to commit block: {addBlockResult}", ErrorCodes.InternalError);
        }

        // forceUpdateHeadBlock: true is required for post-merge chains where TotalDifficulty=0
        // and TTD != 0; without it MoveToMain skips UpdateHeadBlock and the next commit
        // reads a stale head.
        blockTree.TryUpdateMainChain(processedBlock.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: [processedBlock]);

        if (_logger.IsDebug) _logger.Debug($"testing_commitBlockV1 committed block {processedBlock.Header.ToString(BlockHeader.Format.Short)} with hash {processedBlock.Hash}");
        return ResultWrapper<Hash256>.Success(processedBlock.Hash);
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

    private static IEnumerable<Transaction> DecodeTransactions(IEnumerable<byte[]> txRlps)
    {
        foreach (byte[] txRlp in txRlps)
            yield return Rlp.Decode<Transaction>(txRlp, RlpBehaviors.SkipTypedWrapping)
                ?? throw new RlpException("Transaction decoding returned null.");
    }

    private static object CreateGetPayloadResult(Block processedBlock, UInt256 blockFees, IReleaseSpec spec)
    {
        processedBlock.ExecutionRequests ??= ExecutionRequestExtensions.EmptyRequests;
        processedBlock.Header.RequestsHash ??= ExecutionRequestExtensions.EmptyRequestsHash;

        return spec.IsEip7928Enabled
            ? new GetPayloadV6DirectResponse(processedBlock, blockFees, new BlobsBundleV2(processedBlock), processedBlock.ExecutionRequests, shouldOverrideBuilder: false)
            : new GetPayloadV5DirectResponse(processedBlock, blockFees, new BlobsBundleV2(processedBlock), processedBlock.ExecutionRequests, shouldOverrideBuilder: false);
    }
}
