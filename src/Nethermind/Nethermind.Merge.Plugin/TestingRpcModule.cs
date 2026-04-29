// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
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
    private static readonly TimeSpan CommitHeadTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = logManager.GetClassLogger<TestingRpcModule>();
    private readonly SemaphoreSlim _commitLock = new(1, 1);

    public void Dispose() => _commitLock.Dispose();

    public async Task<ResultWrapper<object>> testing_buildBlockV1(Hash256 parentBlockHash, PayloadAttributes payloadAttributes, IEnumerable<byte[]>? txRlps, byte[]? extraData = null)
    {
        Block? parentBlock = blockFinder.FindBlock(parentBlockHash);
        if (parentBlock is null)
            return ResultWrapper<object>.Fail("unknown parent block", MergeErrorCodes.InvalidPayloadAttributes);

        ResultWrapper<ProducedBlock> produced = await ProduceBlockAsync(parentBlock.Header, payloadAttributes, txRlps, extraData, nameof(testing_buildBlockV1), processExitSource.Token);
        if (produced.Result.ResultType == ResultType.Failure)
            return ResultWrapper<object>.Fail(produced.Result.Error!, produced.ErrorCode);

        if (_logger.IsDebug) _logger.Debug($"testing_buildBlockV1 produced payload for block {produced.Data.Block.Header.ToString(BlockHeader.Format.Short)}.");
        return ResultWrapper<object>.Success(CreateGetPayloadResult(produced.Data.Block, produced.Data.Fees, produced.Data.Spec));
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

            ResultWrapper<ProducedBlock> produced = await ProduceBlockAsync(chainHead, payloadAttributes, txRlps, extraData, nameof(testing_commitBlockV1), exitToken);
            if (produced.Result.ResultType == ResultType.Failure)
                return ResultWrapper<Hash256>.Fail(produced.Result.Error!, produced.ErrorCode);

            return await SuggestAndWaitForHeadAsync(produced.Data.Block, exitToken);
        }
        finally
        {
            _commitLock.Release();
        }
    }

    private readonly record struct ProducedBlock(Block Block, UInt256 Fees, IReleaseSpec Spec);

    private async Task<ResultWrapper<ProducedBlock>> ProduceBlockAsync(
        BlockHeader parent,
        PayloadAttributes payloadAttributes,
        IEnumerable<byte[]>? txRlps,
        byte[]? extraData,
        string operationName,
        CancellationToken cancellationToken)
    {
        IReleaseSpec spec = specProvider.GetSpec(new ForkActivation(parent.Number + 1, payloadAttributes.Timestamp));
        BlockHeader header = PrepareBlockHeader(parent, payloadAttributes, spec, extraData);

        await using ScopedBlockProducerEnv env = blockProducerEnvFactory.CreateTransient();

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

        FeesTracer feesTracer = new();
        Block? processedBlock = env.ChainProcessor.Process(block, ProcessingOptions.ProducingBlock, feesTracer, cancellationToken);

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

        return ResultWrapper<ProducedBlock>.Success(new ProducedBlock(processedBlock, feesTracer.Fees, spec));
    }

    private async Task<ResultWrapper<Hash256>> SuggestAndWaitForHeadAsync(Block processedBlock, CancellationToken exitToken)
    {
        if (processedBlock.Hash is null)
            return ResultWrapper<Hash256>.Fail("processed block has no hash", ErrorCodes.InternalError);

        Hash256 expectedHash = processedBlock.Hash;
        TaskCompletionSource<bool> headAdvanced = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNewHead(object? _, BlockEventArgs args)
        {
            if (args.Block.Hash == expectedHash)
                headAdvanced.TrySetResult(true);
        }

        blockTree.NewHeadBlock += OnNewHead;

        try
        {
            if (blockTree.Head?.Hash == expectedHash)
                headAdvanced.TrySetResult(true);

            AddBlockResult addBlockResult = await blockTree.SuggestBlockAsync(processedBlock, BlockTreeSuggestOptions.ShouldProcess);
            if (addBlockResult != AddBlockResult.Added)
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to commit block: {addBlockResult}");
                return ResultWrapper<Hash256>.Fail($"failed to commit block: {addBlockResult}", ErrorCodes.InternalError);
            }

            using CancellationTokenSource timeoutCts = new(CommitHeadTimeout);
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, exitToken);
            try
            {
                await headAdvanced.Task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                string message = exitToken.IsCancellationRequested
                    ? "node is shutting down"
                    : $"block was suggested but did not become head within {CommitHeadTimeout.TotalSeconds:0}s";
                return ResultWrapper<Hash256>.Fail(message, ErrorCodes.InternalError);
            }
        }
        finally
        {
            blockTree.NewHeadBlock -= OnNewHead;
        }

        if (_logger.IsDebug) _logger.Debug($"testing_commitBlockV1 committed block {processedBlock.Header.ToString(BlockHeader.Format.Short)} with hash {expectedHash}");
        return ResultWrapper<Hash256>.Success(expectedHash);
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
