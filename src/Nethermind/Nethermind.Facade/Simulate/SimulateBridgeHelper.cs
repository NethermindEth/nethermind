// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.Proofs;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.Facade.Simulate;

public class SimulateBridgeHelper(SimulateReadOnlyBlocksProcessingEnvFactory simulateProcessingEnvFactory, IBlocksConfig blocksConfig)
{
    private const ProcessingOptions SimulateProcessingOptions =
        ProcessingOptions.ForceProcessing
        | ProcessingOptions.IgnoreParentNotOnMainChain
        | ProcessingOptions.MarkAsProcessed
        | ProcessingOptions.StoreReceipts;

    private void PrepareState(BlockHeader blockHeader,
        BlockHeader parent,
        BlockStateCall<TransactionWithSourceDetails> blockStateCall,
        IWorldState stateProvider,
        OverridableCodeInfoRepository codeInfoRepository,
        IReleaseSpec releaseSpec)
    {
        stateProvider.StateRoot = parent.StateRoot!;
        stateProvider.ApplyStateOverrides(codeInfoRepository, blockStateCall.StateOverrides, releaseSpec, blockHeader.Number);

        IEnumerable<Address> senders = blockStateCall.Calls?.Select(details => details.Transaction.SenderAddress) ?? Enumerable.Empty<Address?>();
        IEnumerable<Address> targets = blockStateCall.Calls?.Select(details => details.Transaction.To!) ?? Enumerable.Empty<Address?>();
        foreach (Address address in senders.Union(targets).Where(t => t is not null))
        {
            stateProvider.CreateAccountIfNotExists(address, 0, 1);
        }

        stateProvider.Commit(releaseSpec);
        stateProvider.CommitTree(blockHeader.Number - 1);
        stateProvider.RecalculateStateRoot();

        blockHeader.StateRoot = stateProvider.StateRoot;
    }

    public bool TrySimulate(
        BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        SimulateBlockTracer simulateOutputTracer,
        IBlockTracer tracer,
        [NotNullWhen(false)] out string? error) =>
        TrySimulate(parent, payload, simulateOutputTracer, tracer, simulateProcessingEnvFactory.Create(payload.Validation), out error);


    private bool TrySimulate(
        BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        SimulateBlockTracer simulateOutputTracer,
        IBlockTracer tracer,
        SimulateReadOnlyBlocksProcessingEnv env,
        [NotNullWhen(false)] out string? error)
    {
        IBlockTree blockTree = env.BlockTree;
        IWorldState stateProvider = env.WorldState;
        parent = GetParent(parent, payload, blockTree);
        IReleaseSpec spec = env.SpecProvider.GetSpec(parent);

        if (payload.BlockStateCalls is not null)
        {
            Dictionary<Address, UInt256> nonceCache = new();
            List<Block> suggestedBlocks = [null];

            foreach (BlockStateCall<TransactionWithSourceDetails> blockCall in payload.BlockStateCalls)
            {
                nonceCache.Clear();

                BlockHeader callHeader = GetCallHeader(blockCall, parent, payload.Validation, spec); //currentSpec is still parent spec
                spec = env.SpecProvider.GetSpec(callHeader);
                PrepareState(callHeader, parent, blockCall, env.WorldState, env.CodeInfoRepository, spec);
                Transaction[] transactions = CreateTransactions(payload, blockCall, callHeader, stateProvider, nonceCache);
                callHeader.TxRoot = TxTrie.CalculateRoot(transactions);
                callHeader.Hash = callHeader.CalculateHash();

                if (!TryGetBlock(payload, env, callHeader, transactions, out Block currentBlock, out error))
                {
                    return false;
                }

                ProcessingOptions processingFlags = payload.Validation
                    ? SimulateProcessingOptions
                    : SimulateProcessingOptions | ProcessingOptions.NoValidation;

                suggestedBlocks[0] = currentBlock;

                IBlockProcessor processor = env.GetProcessor(payload.Validation, spec.IsEip4844Enabled ? blockCall.BlockOverrides?.BlobBaseFee : null);
                Block processedBlock = processor.Process(stateProvider.StateRoot, suggestedBlocks, processingFlags, tracer)[0];

                FinalizeStateAndBlock(stateProvider, processedBlock, spec, currentBlock, blockTree);
                CheckMisssingAndSetTracedDefaults(simulateOutputTracer, processedBlock);

                parent = processedBlock.Header;
            }
        }

        error = null;
        return true;
    }

    private static void CheckMisssingAndSetTracedDefaults(SimulateBlockTracer simulateOutputTracer, Block processedBlock)
    {
        SimulateBlockResult current = simulateOutputTracer.Results.Last();
        current.StateRoot = processedBlock.StateRoot ?? Hash256.Zero;
        current.ParentBeaconBlockRoot = processedBlock.ParentBeaconBlockRoot ?? Hash256.Zero;
        current.TransactionsRoot = processedBlock.Header.TxRoot;
        current.WithdrawalsRoot = processedBlock.WithdrawalsRoot ?? Keccak.EmptyTreeHash;
        current.ExcessBlobGas = processedBlock.ExcessBlobGas ?? 0;
        current.Withdrawals = processedBlock.Withdrawals ?? [];
        current.Author = null;
    }

    private static void FinalizeStateAndBlock(IWorldState stateProvider, Block processedBlock, IReleaseSpec currentSpec, Block currentBlock, IBlockTree blockTree)
    {
        stateProvider.StateRoot = processedBlock.StateRoot!;
        stateProvider.Commit(currentSpec);
        stateProvider.CommitTree(currentBlock.Number);
        blockTree.SuggestBlock(processedBlock, BlockTreeSuggestOptions.ForceSetAsMain);
        blockTree.UpdateHeadBlock(processedBlock.Hash!);
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
            Block? searchResult = blockTree.FindBlock((long)firstBlock.BlockOverrides.Number);
            if (searchResult is not null)
            {
                parent = searchResult.Header;
            }
        }

        return parent;
    }

    private static bool TryGetBlock(
        SimulatePayload<TransactionWithSourceDetails> payload,
        SimulateReadOnlyBlocksProcessingEnv env,
        BlockHeader callHeader,
        Transaction[] transactions,
        out Block currentBlock,
        [NotNullWhen(false)] out string? error)
    {
        IWorldState stateProvider = env.WorldState;
        Snapshot shoot = stateProvider.TakeSnapshot();
        currentBlock = new Block(callHeader);
        LinkedHashSet<Transaction> testedTxs = new();
        for (int index = 0; index < transactions.Length; index++)
        {
            Transaction transaction = transactions[index];
            BlockProcessor.AddingTxEventArgs? args = env.BlockTransactionPicker.CanAddTransaction(currentBlock, transaction, testedTxs, stateProvider);

            if (args.Action is BlockProcessor.TxAction.Stop or BlockProcessor.TxAction.Skip && payload.Validation)
            {
                error = $"invalid transaction index: {index} at block number: {callHeader.Number}, Reason: {args.Reason}";
                return false;
            }

            stateProvider.IncrementNonce(transaction.SenderAddress!);
            testedTxs.Add(transaction);
        }

        stateProvider.Restore(shoot);
        stateProvider.RecalculateStateRoot();

        currentBlock = currentBlock.WithReplacedBody(currentBlock.Body.WithChangedTransactions(testedTxs.ToArray()));
        error = null;
        return true;
    }

    private Transaction[] CreateTransactions(SimulatePayload<TransactionWithSourceDetails> payload,
        BlockStateCall<TransactionWithSourceDetails> callInputBlock,
        BlockHeader callHeader,
        IWorldState stateProvider,
        Dictionary<Address, UInt256> nonceCache)
    {
        int notSpecifiedGasTxsCount = callInputBlock.Calls?.Count(details => !details.HadGasLimitInRequest) ?? 0;
        long gasSpecified = callInputBlock.Calls?.Where(details => details.HadGasLimitInRequest).Sum(details => details.Transaction.GasLimit) ?? 0;
        if (notSpecifiedGasTxsCount > 0)
        {
            long gasPerTx = callHeader.GasLimit - gasSpecified / notSpecifiedGasTxsCount;
            IEnumerable<TransactionWithSourceDetails> notSpecifiedGasTxs = callInputBlock.Calls?.Where(details => !details.HadGasLimitInRequest) ?? Enumerable.Empty<TransactionWithSourceDetails>();
            foreach (TransactionWithSourceDetails call in notSpecifiedGasTxs)
            {
                call.Transaction.GasLimit = gasPerTx;
            }
        }

        return callInputBlock.Calls?.Select(t => CreateTransaction(t, callHeader, stateProvider, nonceCache, payload.Validation)).ToArray() ?? Array.Empty<Transaction>();
    }

    private Transaction CreateTransaction(TransactionWithSourceDetails transactionDetails,
        BlockHeader callHeader,
        IWorldState stateProvider,
        Dictionary<Address, UInt256> nonceCache,
        bool validate)
    {
        Transaction? transaction = transactionDetails.Transaction;
        transaction.SenderAddress ??= Address.Zero;
        transaction.Data ??= Memory<byte>.Empty;

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

        if (validate)
        {
            if (transaction.GasPrice == 0)
            {
                transaction.GasPrice = callHeader.BaseFeePerGas;
            }

            if (transaction.Type == TxType.EIP1559 && transaction.DecodedMaxFeePerGas == 0)
            {
                transaction.DecodedMaxFeePerGas = transaction.GasPrice == 0
                    ? callHeader.BaseFeePerGas + 1
                    : transaction.GasPrice;
            }
        }

        transaction.Hash ??= transaction.CalculateHash();
        callHeader.BlobGasUsed += BlobGasCalculator.CalculateBlobGas(transaction);

        return transaction;
    }

    private BlockHeader GetCallHeader(BlockStateCall<TransactionWithSourceDetails> block, BlockHeader parent, bool payloadValidation, IReleaseSpec spec)
    {
        BlockHeader result = block.BlockOverrides is not null
            ? block.BlockOverrides.GetBlockHeader(parent, blocksConfig, spec)
            : new BlockHeader(
                parent.Hash!,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                UInt256.Zero,
                parent.Number + 1,
                parent.GasLimit,
                parent.Timestamp + 1,
                Array.Empty<byte>())
            {
                MixHash = parent.MixHash,
                IsPostMerge = parent.Difficulty == 0,
            };
        result.Timestamp = parent.Timestamp + 1;
        result.BaseFeePerGas = block.BlockOverrides is { BaseFeePerGas: not null }
            ? block.BlockOverrides.BaseFeePerGas.Value
            : !payloadValidation
                ? 0
                : BaseFeeCalculator.Calculate(parent, spec);

        result.ExcessBlobGas = spec.IsEip4844Enabled ? BlobGasCalculator.CalculateExcessBlobGas(parent, spec) : (ulong?)0;

        return result;
    }
}
