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

    // Reused across calls: serialised by _commitLock, so a single instance is safe.
    // Matches the pattern BlockProcessor.ReceiptsTracer uses in the main pipeline.
    private readonly BlockReceiptsTracer _receiptsTracer = new();

    // CreatePersistent's lifetime is tied to the root container; the testing
    // endpoint reuses one producer env across every call instead of spinning a
    // fresh DI scope per block. BranchProcessor.Process opens a new world-state
    // scope on entry, so there's no cross-call mutable state to leak.
    private IBlockProducerEnv? _env;
    private IBlockProducerEnv Env => _env ??= blockProducerEnvFactory.CreatePersistent();

    public void Dispose() => _commitLock.Dispose();

    public async Task<ResultWrapper<object>> testing_buildBlockV1(Hash256 parentBlockHash, PayloadAttributes payloadAttributes, IEnumerable<byte[]>? txRlps, byte[]? extraData = null)
    {
        Block? parentBlock = blockFinder.FindBlock(parentBlockHash);
        if (parentBlock is null)
            return ResultWrapper<object>.Fail("unknown parent block", MergeErrorCodes.InvalidPayloadAttributes);

        FeesTracer feesTracer = new();
        ResultWrapper<ProducedBlock> produced = await ProduceBlockAsync(
            parentBlock.Header, payloadAttributes, txRlps, extraData,
            nameof(testing_buildBlockV1), processExitSource.Token,
            feesTracer, ProcessingOptions.ProducingBlock);
        if (produced.Result.ResultType == ResultType.Failure)
            return ResultWrapper<object>.Fail(produced.Result.Error!, produced.ErrorCode);

        if (_logger.IsDebug) _logger.Debug($"testing_buildBlockV1 produced payload for block {produced.Data.Block.Header.ToString(BlockHeader.Format.Short)}.");
        return ResultWrapper<object>.Success(CreateGetPayloadResult(produced.Data.Block, feesTracer.Fees, produced.Data.Spec));
    }

    public async Task<ResultWrapper<Hash256>> testing_commitBlockV1(
        PayloadAttributes payloadAttributes, IEnumerable<byte[]> txRlps, byte[]? extraData = null)
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

            // StoreReceipts moves receipt persistence from the main BlockProcessor
            // (which we no longer run) into the producer pass. The receipts tracer
            // collects them; the inner BlockProcessor inserts them into the
            // receipt store when the StoreReceipts flag is set.
            //
            // ProducingBlock's ReadOnlyChain bit is intentionally dropped here:
            // ReadOnlyChain makes FlatWorldStateScope open in _isReadOnly mode
            // and AddSnapshot is gated on !_isReadOnly (FlatWorldStateScope.cs:203).
            // Without FlatDb snapshots being appended, the next commit's
            // BeginScope(parent) fails with "Unable to gather snapshots". Pass F
            // used to compensate by re-executing with the read-write flag set;
            // since we skip pass F now, the producer must be the one that
            // commits to FlatDb. NoValidation + ForceProcessing + DoNotUpdateHead
            // preserve the rest of ProducingBlock's semantics.
            const ProcessingOptions ProducerOptions =
                ProcessingOptions.NoValidation
                | ProcessingOptions.ForceProcessing
                | ProcessingOptions.DoNotUpdateHead
                | ProcessingOptions.StoreReceipts;

            _receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
            ResultWrapper<ProducedBlock> produced = await ProduceBlockAsync(
                chainHead, payloadAttributes, txRlps, extraData,
                nameof(testing_commitBlockV1), exitToken,
                _receiptsTracer, ProducerOptions);
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

    private Task<ResultWrapper<ProducedBlock>> ProduceBlockAsync(
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

        IBlockProducerEnv env = Env;

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
            return Task.FromResult(ResultWrapper<ProducedBlock>.Fail($"invalid transaction RLP: {e.Message}", ErrorCodes.InvalidInput));
        }

        header.TxRoot = TxTrie.CalculateRoot(transactions);
        BlockToProduce block = new(header, transactions, [], spec.WithdrawalsEnabled ? (payloadAttributes.Withdrawals ?? []) : null);

        Block? processedBlock = env.ChainProcessor.Process(block, options, tracer, cancellationToken);

        if (processedBlock is null)
            return Task.FromResult(cancellationToken.IsCancellationRequested
                ? ResultWrapper<ProducedBlock>.Fail("node is shutting down", ErrorCodes.InternalError)
                : ResultWrapper<ProducedBlock>.Fail("payload processing failed", ErrorCodes.InternalError));

        if (txRlps is not null && processedBlock.Transactions.Length != transactions.Length)
        {
            string error = $"expected {transactions.Length} transactions but only {processedBlock.Transactions.Length} were included";
            if (_logger.IsWarn) _logger.Warn($"{operationName} failed: {error}");
            return Task.FromResult(ResultWrapper<ProducedBlock>.Fail(error, ErrorCodes.InvalidInput));
        }

        return Task.FromResult(ResultWrapper<ProducedBlock>.Success(new ProducedBlock(processedBlock, spec)));
    }

    /// <summary>
    /// Add the already-processed block to the chain and advance the canonical head.
    /// Skips the main BlockchainProcessor (which would re-execute every transaction):
    /// the producer's pass already committed the state trie and persisted receipts via
    /// <see cref="ProcessingOptions.StoreReceipts"/>. UpdateMainChain fires
    /// <c>NewHeadBlock</c>, <c>BlockAddedToMain</c>, and <c>OnUpdateMainChain</c>
    /// synchronously on this thread; downstream consumers wired to <c>BlockProcessed</c>
    /// (FilterManager, BackgroundTaskScheduler, AuRa finalisation) do not fire — this
    /// endpoint is testing-only and callers do not depend on those events.
    /// </summary>
    private ResultWrapper<Hash256> CommitAsMainChain(Block processedBlock)
    {
        if (processedBlock.Hash is null)
            return ResultWrapper<Hash256>.Fail("processed block has no hash", ErrorCodes.InternalError);

        AddBlockResult addBlockResult = blockTree.SuggestBlock(processedBlock, BlockTreeSuggestOptions.None);
        if (addBlockResult != AddBlockResult.Added)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to commit block: {addBlockResult}");
            return ResultWrapper<Hash256>.Fail($"failed to commit block: {addBlockResult}", ErrorCodes.InternalError);
        }

        blockTree.UpdateMainChain([processedBlock], wereProcessed: true);

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
            yield return Rlp.Decode<Transaction>(txRlp, RlpBehaviors.SkipTypedWrapping);
    }

    private static object CreateGetPayloadResult(Block processedBlock, UInt256 blockFees, IReleaseSpec spec)
    {
        processedBlock.ExecutionRequests ??= ExecutionRequestExtensions.EmptyRequests;
        processedBlock.Header.RequestsHash ??= ExecutionRequestExtensions.EmptyRequestsHash;

        return spec.IsEip7928Enabled
            ? new GetPayloadV6Result(processedBlock, blockFees, new BlobsBundleV2(processedBlock), processedBlock.ExecutionRequests, shouldOverrideBuilder: false)
            : new GetPayloadV5Result(processedBlock, blockFees, new BlobsBundleV2(processedBlock), processedBlock.ExecutionRequests, shouldOverrideBuilder: false);
    }
}
