// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.State;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.Facade.Simulate;

public class SimulateBridgeHelper(IBlocksConfig blocksConfig, ISpecProvider specProvider)
{
    private const ProcessingOptions SimulateProcessingOptions =
        ProcessingOptions.ForceProcessing
        | ProcessingOptions.IgnoreParentNotOnMainChain
        | ProcessingOptions.MarkAsProcessed
        | ProcessingOptions.StoreReceipts;

    private void PrepareState(
        BlockStateCall<TransactionWithSourceDetails> blockStateCall,
        IWorldState stateProvider,
        IOverridableCodeInfoRepository codeInfoRepository,
        long blockNumber,
        IReleaseSpec releaseSpec)
    {
        stateProvider.ApplyStateOverridesNoCommit(codeInfoRepository, blockStateCall.StateOverrides, releaseSpec);

        IEnumerable<Address> senders = blockStateCall.Calls?.Select(static details => details.Transaction.SenderAddress) ?? [];
        IEnumerable<Address> targets = blockStateCall.Calls?.Select(static details => details.Transaction.To!) ?? [];
        foreach (Address address in senders.Union(targets).Where(static t => t is not null))
        {
            stateProvider.CreateAccountIfNotExists(address, 0, 0);
        }

        stateProvider.Commit(releaseSpec, commitRoots: true);
        stateProvider.CommitTree(blockNumber);
    }

    public SimulateOutput<TTrace> TrySimulate<TTrace>(
        BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer,
        SimulateReadOnlyBlocksProcessingScope env,
        long gasCapLimit,
        CancellationToken cancellationToken)
    {
        List<SimulateBlockResult<TTrace>> list = [];
        SimulateOutput<TTrace> result = new()
        {
            Items = list
        };

        try
        {
            Simulate(parent, payload, tracer, env, list, onBlockComplete: null, gasCapLimit, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            result.Error = ex.Message;
            result.IsInvalidInput = true;
        }
        catch (InvalidTransactionException ex)
        {
            result.Error = ex.Reason.ErrorDescription;
            result.TransactionResult = ex.Reason;
        }
        catch (InsufficientBalanceException ex)
        {
            result.Error = ex.Message;
            result.TransactionResult = TransactionResult.InsufficientSenderBalance;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Streaming variant: emits each completed <see cref="SimulateBlockResult{TTrace}"/> via
    /// <paramref name="onBlockComplete"/> as soon as <c>BlockProcessor.ProcessOne</c> returns
    /// for that block, without accumulating a cross-block list. The handler is invoked inline
    /// on the simulate execution thread; exceptions propagate so the streaming envelope can
    /// surface them via <see cref="SimulateEnvelopeWriter.WriteFailureObject"/>.
    /// <para>
    /// The peak in-flight payload is bounded by <i>one</i> <c>SimulateBlockResult</c> plus
    /// its inner <c>Calls</c> list — the previous block is GC-eligible the moment the
    /// handler returns and the next block starts processing.
    /// </para>
    /// </summary>
    public void TrySimulateStreaming<TTrace>(
        BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer,
        SimulateReadOnlyBlocksProcessingScope env,
        Action<SimulateBlockResult<TTrace>> onBlockComplete,
        long gasCapLimit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onBlockComplete);
        Simulate(parent, payload, tracer, env, output: null, onBlockComplete, gasCapLimit, cancellationToken);
    }

    private void Simulate<TTrace>(BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer,
        SimulateReadOnlyBlocksProcessingScope env,
        List<SimulateBlockResult<TTrace>>? output,
        Action<SimulateBlockResult<TTrace>>? onBlockComplete,
        long gasCapLimit,
        CancellationToken cancellationToken)
    {
        IBlockTree blockTree = env.BlockTree;
        IWorldState stateProvider = env.WorldState;
        parent = GetParent(parent, payload, blockTree);

        env.SimulateRequestState.TotalGasLeft = gasCapLimit;

        if (payload.BlockStateCalls is not null)
        {
            // Locate the underlying SimulateBlockTracer (if any) up-front. Streaming wraps it
            // in a StreamingSimulateBlockTracer so the existing `is SimulateBlockTracer` check
            // below would miss; we still need to apply the post-processing block hash to logs.
            SimulateBlockTracer? simulateTracer = tracer as SimulateBlockTracer
                ?? (tracer as StreamingSimulateBlockTracer<TTrace>)?.Inner as SimulateBlockTracer;

            Dictionary<Address, ulong> nonceCache = [];
            IBlockTracer cancellationBlockTracer = tracer.WithCancellation(cancellationToken);

            foreach (BlockStateCall<TransactionWithSourceDetails> blockCall in payload.BlockStateCalls)
            {
                nonceCache.Clear();

                (BlockHeader callHeader, IReleaseSpec spec) = GetCallHeader(env.SpecProvider, blockCall, parent, payload.Validation);
                env.SimulateRequestState.BlockGasLeft = callHeader.GasLimit;
                callHeader.Hash = callHeader.CalculateHash();

                TransactionWithSourceDetails[] calls = blockCall.Calls ?? [];

                env.SimulateRequestState.TxsWithExplicitGas = calls
                    .Select((c) => c.HadGasLimitInRequest)
                    .ToArray();

                PrepareState(blockCall, env.WorldState, env.CodeInfoRepository, callHeader.Number, spec);

                BlockBody body = AssembleBody(calls, stateProvider, nonceCache, spec);
                Block callBlock = new(callHeader, body);

                ProcessingOptions processingFlags = payload.Validation
                    ? SimulateProcessingOptions
                    : SimulateProcessingOptions | ProcessingOptions.NoValidation;

                env.SimulateRequestState.Validate = payload.Validation;
                env.SimulateRequestState.BlobBaseFeeOverride = spec.IsEip4844Enabled ? blockCall.BlockOverrides?.BlobBaseFee : null;

                (Block processedBlock, TxReceipt[] receipts) = env.BlockProcessor.ProcessOne(
                    callBlock,
                    processingFlags,
                    cancellationBlockTracer,
                    spec,
                    cancellationToken);

                stateProvider.CommitTree(processedBlock.Number);
                blockTree.SuggestBlock(processedBlock, BlockTreeSuggestOptions.ForceSetAsMain);
                blockTree.UpdateHeadBlock(processedBlock.Hash!);

                if (simulateTracer is not null)
                {
                    simulateTracer.ReapplyBlockHash(processedBlock.Hash);
                }

                // Streaming wrapper builds its result from the inner tracer; the wrapper itself
                // intentionally returns an empty collection from BuildResult so the buffered path
                // can never accidentally accumulate cross-block state on a streaming run.
                IReadOnlyCollection<TTrace> txTraces = tracer is StreamingSimulateBlockTracer<TTrace> streamingTracer
                    ? streamingTracer.Inner.BuildResult()
                    : tracer.BuildResult();

                SimulateBlockResult<TTrace> blockResult = new(processedBlock, payload.ReturnFullTransactionObjects, specProvider)
                {
                    Calls = [.. txTraces],
                };

                if (onBlockComplete is not null)
                {
                    onBlockComplete(blockResult);
                }
                else
                {
                    output!.Add(blockResult);
                }
                parent = processedBlock.Header;
            }
        }
    }

    private BlockBody AssembleBody(
        TransactionWithSourceDetails[] calls,
        IWorldState stateProvider,
        Dictionary<Address, ulong> nonceCache,
        IReleaseSpec spec)
    {
        Transaction[] transactions = calls
            .Select(t => CreateTransaction(t, stateProvider, nonceCache))
            .ToArray();

        Withdrawal[]? withdrawals = null;
        if (spec.WithdrawalsEnabled)
        {
            withdrawals = [];
        }

        BlockBody body = new(transactions, null, withdrawals);
        return body;
    }

    private static BlockHeader GetParent(BlockHeader parent, SimulatePayload<TransactionWithSourceDetails> payload, IBlockTree blockTree)
    {
        Block? latestBlock = blockTree.FindLatestBlock();
        long latestBlockNumber = latestBlock?.Number ?? 0;

        if (latestBlockNumber < parent.Number)
        {
            parent = latestBlock?.Header ?? blockTree.Head!.Header;
        }

        BlockStateCall<TransactionWithSourceDetails>? firstBlock = payload.BlockStateCalls?.FirstOrDefault();

        ulong lastKnown = (ulong)latestBlockNumber;
        if (firstBlock?.BlockOverrides?.Number > 0 && firstBlock.BlockOverrides?.Number < lastKnown)
        {
            Block? searchResult = blockTree.FindBlock((long)firstBlock.BlockOverrides.Number - 1);
            if (searchResult is not null)
            {
                parent = searchResult.Header;
            }
        }

        return parent;
    }

    private Transaction CreateTransaction(
        TransactionWithSourceDetails transactionDetails,
        IWorldState stateProvider,
        Dictionary<Address, ulong> nonceCache)
    {
        Transaction? transaction = transactionDetails.Transaction;
        transaction.SenderAddress ??= Address.Zero;

        if (!transactionDetails.HadNonceInRequest)
        {
            ref ulong cachedNonce = ref CollectionsMarshal.GetValueRefOrAddDefault(nonceCache, transaction.SenderAddress, out bool exist);
            if (!exist)
            {
                if (stateProvider.TryGetAccount(transaction.SenderAddress, out AccountStruct test))
                {
                    cachedNonce = test.Nonce.ToUInt64(null);
                }
                // else // Todo think if we shall create account here
            }
            else
            {
                cachedNonce++;
            }

            transaction.Nonce = cachedNonce;
        }

        if (transaction.SupportsBlobs && transaction.BlobVersionedHashes is null) transaction.BlobVersionedHashes = [];
        if (transaction.AccessList is not null && transaction.AccessList.IsEmpty) transaction.AccessList = null;
        transaction.Hash ??= transaction.CalculateHash();

        return transaction;
    }

    private (BlockHeader, IReleaseSpec) GetCallHeader(
        ISpecProvider specProvider,
        BlockStateCall<TransactionWithSourceDetails> block,
        BlockHeader parent,
        bool validate)
    {
        BlockHeader result = parent.CreateSimulatedChild(parent.Timestamp + blocksConfig.SecondsPerSlot);

        if ((ForkActivation)result.Number >= specProvider.MergeBlockNumber)
        {
            result.IsPostMerge = true;
        }
        else
        {
            result.Difficulty = parent.Difficulty;
            result.IsPostMerge = false;
        }

        IReleaseSpec spec = specProvider.GetSpec(result);

        if (spec.WithdrawalsEnabled) result.WithdrawalsRoot = Keccak.EmptyTreeHash;
        if (spec.IsBeaconBlockRootAvailable) result.ParentBeaconBlockRoot = Hash256.Zero;

        // In non-validation mode base fee is set to 0 if it is not overridden.
        // This is because it creates an edge case in EVM where gasPrice < baseFee.
        // Base fee could have been overridden.
        if (validate)
        {
            result.BaseFeePerGas = spec.BaseFeeCalculator.Calculate(parent, spec);
        }
        else
        {
            result.BaseFeePerGas = 0;
        }

        result.ExcessBlobGas = spec.IsEip4844Enabled ? BlobGasCalculator.CalculateExcessBlobGas(parent, spec) : null;

        block.BlockOverrides?.ApplyOverrides(result);

        return (result, spec);
    }
}
