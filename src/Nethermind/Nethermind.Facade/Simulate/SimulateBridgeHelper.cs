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
using Nethermind.Int256;
using Nethermind.State;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Evm.State;
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
        IReleaseSpec releaseSpec)
    {
        stateProvider.ApplyStateOverridesNoCommit(codeInfoRepository, blockStateCall.StateOverrides, releaseSpec);

        IEnumerable<Address> senders = blockStateCall.Calls?.Select(static details => details.Transaction.SenderAddress) ?? [];
        IEnumerable<Address> targets = blockStateCall.Calls?.Select(static details => details.Transaction.To!) ?? [];
        foreach (Address address in senders.Union(targets).Where(static t => t is not null))
        {
            stateProvider.CreateAccountIfNotExists(address, 0, 0);
        }
    }

    public SimulateOutput<TTrace> TrySimulate<TTrace>(
        BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer,
        SimulateReadOnlyBlocksProcessingScope env,
        long gasCapLimit,
        CancellationToken cancellationToken)
    {
        List<SimulateBlockResult<TTrace>> list = new();
        SimulateOutput<TTrace> result = new SimulateOutput<TTrace>()
        {
            Items = list
        };

        try
        {
            if (!TrySimulate(parent, payload, tracer, env, list, gasCapLimit, cancellationToken, out string? error))
            {
                result.Error = error;
            }
        }
        catch (InsufficientBalanceException ex)
        {
            result.Error = ex.Message;
        }
        catch (Exception ex)
        {
            result.Error = ex.ToString();
        }

        return result;
    }

    private bool TrySimulate<TTrace>(BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer,
        SimulateReadOnlyBlocksProcessingScope env,
        List<SimulateBlockResult<TTrace>> output,
        long gasCapLimit,
        CancellationToken cancellationToken,
        [NotNullWhen(false)] out string? error)
    {
        IBlockTree blockTree = env.BlockTree;
        IWorldState stateProvider = env.WorldState;
        parent = GetParent(parent, payload, blockTree);

        env.SimulateRequestState.TotalGasLeft = long.Min(parent.GasLimit, gasCapLimit);

        if (payload.BlockStateCalls is not null)
        {
            Dictionary<Address, UInt256> nonceCache = new();
            IBlockTracer cancellationBlockTracer = tracer.WithCancellation(cancellationToken);

            foreach (BlockStateCall<TransactionWithSourceDetails> blockCall in payload.BlockStateCalls)
            {
                nonceCache.Clear();

                (BlockHeader callHeader, IReleaseSpec spec) = GetCallHeader(env.SpecProvider, blockCall, parent, payload.Validation);
                callHeader.Hash = callHeader.CalculateHash();

                TransactionWithSourceDetails[] calls = blockCall.Calls ?? [];

                env.SimulateRequestState.TxsWithExplicitGas = calls
                    .Select((c) => c.HadGasLimitInRequest)
                    .ToArray();

                BlockBody body = AssembleBody(calls, stateProvider, nonceCache, spec);
                Block callBlock = new Block(callHeader, body);

                PrepareState(blockCall, env.WorldState, env.CodeInfoRepository, spec);

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

                if (tracer is SimulateBlockMutatorTracer simulateTracer)
                {
                    simulateTracer.ReapplyBlockHash();
                }

                SimulateBlockResult<TTrace> blockResult = new(processedBlock, payload.ReturnFullTransactionObjects, specProvider)
                {
                    Calls = [.. tracer.BuildResult()],
                };

                output.Add(blockResult);
                parent = processedBlock.Header;
            }
        }

        error = null;
        return true;
    }

    private BlockBody AssembleBody(
        TransactionWithSourceDetails[] calls,
        IWorldState stateProvider,
        Dictionary<Address, UInt256> nonceCache,
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

        BlockBody body = new BlockBody(transactions, null, withdrawals);
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
        Dictionary<Address, UInt256> nonceCache)
    {
        Transaction? transaction = transactionDetails.Transaction;
        transaction.SenderAddress ??= Address.Zero;

        if (!transactionDetails.HadNonceInRequest)
        {
            ref UInt256 cachedNonce = ref CollectionsMarshal.GetValueRefOrAddDefault(nonceCache, transaction.SenderAddress, out bool exist);
            if (!exist)
            {
                if (stateProvider.TryGetAccount(transaction.SenderAddress, out AccountStruct test))
                {
                    cachedNonce = test.Nonce;
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
        BlockHeader result = new BlockHeader(
            parent.Hash!,
            Keccak.OfAnEmptySequenceRlp,
            parent.Beneficiary,
            UInt256.Zero,
            parent.Number + 1,
            parent.GasLimit,
            parent.Timestamp + blocksConfig.SecondsPerSlot,
            [],
            requestsHash: parent.RequestsHash)
        {
            MixHash = parent.MixHash,
            IsPostMerge = parent.Difficulty == 0,
            RequestsHash = parent.RequestsHash,
        };

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

        result.ExcessBlobGas = spec.IsEip4844Enabled ? BlobGasCalculator.CalculateExcessBlobGas(parent, spec) : (ulong?)0;

        block.BlockOverrides?.ApplyOverrides(result);

        return (result, spec);
    }
}
