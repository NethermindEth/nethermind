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
        ulong blockNumber,
        IReleaseSpec releaseSpec)
    {
        // state-override commits must not trigger EIP-158 deletion on accounts whose
        // code/nonce were zeroed while storage remains — EIP-7610 collision checks need that storage.
        releaseSpec = releaseSpec.WithoutEip158();

        stateProvider.ApplyStateOverridesNoCommit(codeInfoRepository, blockStateCall.StateOverrides, releaseSpec);

        TransactionWithSourceDetails[]? calls = blockStateCall.Calls;
        if (calls is not null)
        {
            if (calls.Length == 1)
            {
                Transaction transaction = calls[0].Transaction;
                Address? sender = transaction.SenderAddress;
                if (sender is not null)
                {
                    stateProvider.CreateAccountIfNotExists(sender, 0, 0);
                }

                Address? to = transaction.To;
                if (to is not null && !Equals(sender, to))
                {
                    stateProvider.CreateAccountIfNotExists(to, 0, 0);
                }
            }
            else
            {
                HashSet<Address> seenAddresses = new(calls.Length * 2, Address.EqualityComparer);
                for (int i = 0; i < calls.Length; i++)
                {
                    Transaction transaction = calls[i].Transaction;
                    CreateAccountIfNotExists(transaction.SenderAddress, stateProvider, seenAddresses);
                    CreateAccountIfNotExists(transaction.To, stateProvider, seenAddresses);
                }
            }
        }

        stateProvider.Commit(releaseSpec, commitRoots: true);
        stateProvider.CommitTree(blockNumber);
    }

    private static void CreateAccountIfNotExists(Address? address, IWorldState stateProvider, HashSet<Address> seenAddresses)
    {
        if (address is not null && seenAddresses.Add(address))
        {
            stateProvider.CreateAccountIfNotExists(address, 0, 0);
        }
    }

    public SimulateOutput<TTrace> TrySimulate<TTrace>(
        BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer,
        SimulateReadOnlyBlocksProcessingScope env,
        ulong gasCapLimit,
        CancellationToken cancellationToken)
    {
        int blockCount = payload.BlockStateCalls?.Count ?? 0;
        List<SimulateBlockResult<TTrace>> list = new(blockCount);
        SimulateOutput<TTrace> result = new()
        {
            Items = list
        };

        try
        {
            Simulate(parent, payload, tracer, env, list, gasCapLimit, cancellationToken);
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

    private void Simulate<TTrace>(BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer,
        SimulateReadOnlyBlocksProcessingScope env,
        List<SimulateBlockResult<TTrace>> output,
        ulong gasCapLimit,
        CancellationToken cancellationToken)
    {
        IBlockTree blockTree = env.BlockTree;
        IWorldState stateProvider = env.WorldState;
        parent = GetParent(parent, payload, blockTree);

        env.SimulateRequestState.TotalGasLeft = gasCapLimit;

        if (payload.BlockStateCalls is not null)
        {
            Dictionary<Address, ulong> nonceCache = [];
            IBlockTracer cancellationBlockTracer = tracer.WithCancellation(cancellationToken);

            foreach (BlockStateCall<TransactionWithSourceDetails> blockCall in payload.BlockStateCalls)
            {
                nonceCache.Clear();

                (BlockHeader callHeader, IReleaseSpec spec) = GetCallHeader(env.SpecProvider, blockCall, parent, payload.Validation);
                env.SimulateRequestState.BlockGasLeft = callHeader.GasLimit;
                callHeader.Hash = callHeader.CalculateHash();

                TransactionWithSourceDetails[] calls = blockCall.Calls ?? [];

                env.SimulateRequestState.SetTxsWithExplicitGas(calls);

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

                Hash256 processedBlockHash = processedBlock.Hash ?? throw new InvalidOperationException("Cannot simulate a processed block without a calculated hash.");

                stateProvider.CommitTree(processedBlock.Number);
                blockTree.SuggestBlock(processedBlock, BlockTreeSuggestOptions.ForceSetAsMain);
                blockTree.UpdateHeadBlock(processedBlockHash);

                if (tracer is SimulateBlockTracer simulateTracer)
                {
                    simulateTracer.ReapplyBlockHash(processedBlockHash);
                }

                SimulateBlockResult<TTrace> blockResult = new(processedBlock, payload.ReturnFullTransactionObjects, specProvider)
                {
                    Calls = [.. tracer.BuildResult()],
                };

                output.Add(blockResult);
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
        Transaction[] transactions = new Transaction[calls.Length];
        for (int i = 0; i < calls.Length; i++)
        {
            transactions[i] = CreateTransaction(calls[i], stateProvider, nonceCache);
        }

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
        ulong latestBlockNumber = latestBlock?.Number ?? 0;

        if (latestBlockNumber < parent.Number)
        {
            parent = latestBlock?.Header ?? blockTree.Head!.Header;
        }

        BlockStateCall<TransactionWithSourceDetails>? firstBlock =
            payload.BlockStateCalls is { Count: > 0 } blockStateCalls
                ? blockStateCalls[0]
                : null;

        ulong lastKnown = latestBlockNumber;
        if (firstBlock?.BlockOverrides?.Number > 0 && firstBlock.BlockOverrides?.Number < lastKnown)
        {
            Block? searchResult = blockTree.FindBlock(firstBlock.BlockOverrides.Number.Value - 1);
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
