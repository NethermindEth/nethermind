// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.State;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.Facade.Simulate;

public class SimulateBridgeHelper(
    SimulateReadOnlyBlocksProcessingEnvFactory simulateProcessingEnvFactory,
    ISpecProvider specProvider,
    IBlocksConfig blocksConfig)
{
    private static readonly ProcessingOptions _simulateProcessingOptions = ProcessingOptions.ForceProcessing |
                                                                           ProcessingOptions.IgnoreParentNotOnMainChain |
                                                                           ProcessingOptions.MarkAsProcessed |
                                                                           ProcessingOptions.StoreReceipts;

    private void UpdateStateByModifyingAccounts(BlockHeader blockHeader,
        BlockStateCall<TransactionWithSourceDetails> blockStateCall, SimulateReadOnlyBlocksProcessingEnv env)
    {
        IReleaseSpec currentSpec = env.SpecProvider.GetSpec(blockHeader);
        env.StateProvider.ApplyStateOverrides(env.CodeInfoRepository, blockStateCall.StateOverrides, currentSpec,
            blockHeader.Number);

        IEnumerable<Address?>? senders = blockStateCall.Calls.Select(details => details.Transaction.SenderAddress);
        IEnumerable<Address?>? targets = blockStateCall.Calls.Select(details => details.Transaction.To!);
        var all = senders.Union(targets)
            .Where(address => address != null)
            .Distinct()
            .ToList();

        foreach (Address address in all)
        {
            env.StateProvider.CreateAccountIfNotExists(address, 0, 0);
        }

        IWorldState? state = env.StateProvider;
        state.Commit(currentSpec);
        state.CommitTree(blockHeader.Number);
        state.RecalculateStateRoot();

        blockHeader.StateRoot = env.StateProvider.StateRoot;
    }

    public (bool Success, string Error) TrySimulateTrace(BlockHeader parent, SimulatePayload<TransactionWithSourceDetails> payload, IBlockTracer tracer) =>
        TrySimulateTrace(parent, payload, tracer, simulateProcessingEnvFactory.Create(payload.TraceTransfers, payload.Validation));


    private (bool Success, string Error) TrySimulateTrace(BlockHeader parent, SimulatePayload<TransactionWithSourceDetails> payload, IBlockTracer tracer, SimulateReadOnlyBlocksProcessingEnv env)
    {
        Block? latestBlock = env.BlockTree.FindLatestBlock();
        long latestBlockNumber = latestBlock?.Number ?? 0;

        if (latestBlockNumber < parent.Number)
        {
            parent = latestBlock?.Header ?? env.BlockTree.Head!.Header;
        }

        IWorldState stateProvider = env.StateProvider;
        stateProvider.StateRoot = parent.StateRoot!;

        BlockStateCall<TransactionWithSourceDetails>? firstBlock = payload.BlockStateCalls?.FirstOrDefault();

        ulong lastKnown = (ulong)latestBlockNumber;
        if (firstBlock?.BlockOverrides?.Number > 0 && firstBlock?.BlockOverrides?.Number < lastKnown)
        {
            Block? searchResult = env.BlockTree.FindBlock((long)firstBlock.BlockOverrides.Number);
            if (searchResult is not null)
            {
                parent = searchResult.Header;
                stateProvider.StateRoot = parent.StateRoot!;
            }
        }

        if (payload.BlockStateCalls is not null)
        {
            Dictionary<Address, UInt256> nonceCache = new();
            List<Block> suggestedBlocks = new();

            foreach (BlockStateCall<TransactionWithSourceDetails> callInputBlock in payload.BlockStateCalls)
            {
                BlockHeader callHeader = callInputBlock.BlockOverrides is not null
                    ? callInputBlock.BlockOverrides.GetBlockHeader(parent, blocksConfig)
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
                        BaseFeePerGas = BaseFeeCalculator.Calculate(parent, specProvider.GetSpec(parent)),
                        MixHash = parent.MixHash,
                        IsPostMerge = parent.Difficulty == 0
                    };

                UpdateStateByModifyingAccounts(callHeader, callInputBlock, env);

                using IReadOnlyTransactionProcessor? readOnlyTransactionProcessor = env.Build(env.StateProvider.StateRoot!);
                
                Transaction SetTxHashAndMissingDefaults(TransactionWithSourceDetails transactionDetails, IReleaseSpec? spec)
                {
                    Transaction? transaction = transactionDetails.Transaction;
                    transaction.SenderAddress ??= Address.Zero;
                    transaction.To ??= Address.Zero;
                    transaction.Data ??= Memory<byte>.Empty;

                    if (!transactionDetails.HadNonceInRequest)
                    {
                        if (!nonceCache.TryGetValue(transaction.SenderAddress, out UInt256 cachedNonce))
                        {
                            cachedNonce = env.StateProvider.GetAccount(transaction.SenderAddress).Nonce;
                            nonceCache[transaction.SenderAddress] = cachedNonce;
                        }
                        else
                        {
                            cachedNonce++;
                            nonceCache[transaction.SenderAddress] = cachedNonce;
                        }

                        transaction.Nonce = cachedNonce;
                    }

                    if (payload.Validation)
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
                            //UInt256.MultiplyOverflow((UInt256)transaction.GasLimit, transaction.MaxFeePerGas, out UInt256 maxGasFee);
                            //string err = $"insufficient sender balance for MaxFeePerGas: {callHeader.Number}, {transaction.SenderAddress} balance should be at least {maxGasFee + 1 + transaction.Value }";
                        }
                    }

                    transaction.Hash ??= transaction.CalculateHash();

                    return transaction;
                }
                IReleaseSpec? spec = specProvider.GetSpec(parent);

                var specifiedGasTxs = callInputBlock.Calls.Where(details => details.HadGasLimitInRequest).ToList();
                var notSpecifiedGasTxs = callInputBlock.Calls.Where(details => !details.HadGasLimitInRequest).ToList();
                var gasSpecified =
                    specifiedGasTxs.Sum(details => details.Transaction.GasLimit);
                if (notSpecifiedGasTxs.Any())
                {
                    var gasPerTx = callHeader.GasLimit - gasSpecified / notSpecifiedGasTxs.Count;
                    foreach (TransactionWithSourceDetails? call in notSpecifiedGasTxs)
                    {
                        call.Transaction.GasLimit = gasPerTx;
                    }
                }

                Transaction[] transactions = callInputBlock.Calls?.Select(t => SetTxHashAndMissingDefaults(t, spec)).ToArray() ?? Array.Empty<Transaction>();


                Block? currentBlock = new(callHeader, transactions, Array.Empty<BlockHeader>());
                currentBlock.Header.Hash = currentBlock.Header.CalculateHash();

                ProcessingOptions processingFlags = _simulateProcessingOptions;

                if (!payload.Validation)
                {
                    processingFlags |= ProcessingOptions.NoValidation;
                }

                suggestedBlocks.Clear();
                suggestedBlocks.Add(currentBlock);

                stateProvider.RecalculateStateRoot();
                Block[]? currentBlocks = null;
                //try
                {
                    IBlockProcessor processor = env.GetProcessor(currentBlock.StateRoot!);
                    currentBlocks = processor.Process(stateProvider.StateRoot, suggestedBlocks, processingFlags, tracer);
                }
                //catch (Exception)
                //{
                //    return (false, $"invalid on block {callHeader.Number}");
                //}

                Block? processedBlock = currentBlocks[0];
                parent = processedBlock.Header;
                if (processedBlock is not null)
                {
                    //env.BlockTree.UpdateMainChain(new[] { currentBlock }, true, true);
                    env.BlockTree.UpdateHeadBlock(currentBlock.Hash!);
                }
            }
        }

        return (true, "");
    }
}
