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
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;

namespace Nethermind.Facade.Multicall;

public class MulticallBridgeHelper
{
    private readonly MultiCallReadOnlyBlocksProcessingEnv _multiCallProcessingEnv;
    private readonly ISpecProvider _specProvider;
    private readonly IBlocksConfig _blocksConfig;

    private static readonly ProcessingOptions _multicallProcessingOptions = ProcessingOptions.ForceProcessing |
                                                                            ProcessingOptions.IgnoreParentNotOnMainChain |
                                                                            ProcessingOptions.MarkAsProcessed |
                                                                            ProcessingOptions.StoreReceipts;

    public MulticallBridgeHelper(MultiCallReadOnlyBlocksProcessingEnv multiCallProcessingEnv, ISpecProvider specProvider, IBlocksConfig blocksConfig)
    {
        _multiCallProcessingEnv = multiCallProcessingEnv;
        _specProvider = specProvider;
        _blocksConfig = blocksConfig;
    }

    private void UpdateStateByModifyingAccounts(BlockHeader blockHeader, BlockStateCall<TransactionWithSourceDetails> blockStateCall, MultiCallReadOnlyBlocksProcessingEnv env)
    {
        IReleaseSpec currentSpec = env.SpecProvider.GetSpec(blockHeader);
        env.StateProvider.ApplyStateOverrides(_multiCallProcessingEnv.CodeInfoRepository, blockStateCall.StateOverrides, currentSpec, blockHeader.Number - 1);
        blockHeader.StateRoot = env.StateProvider.StateRoot;
    }

    public (bool Success, string Error) TryMultiCallTrace(BlockHeader parent, MultiCallPayload<TransactionWithSourceDetails> payload, IBlockTracer tracer)
    {
        using MultiCallReadOnlyBlocksProcessingEnv? env = _multiCallProcessingEnv.Clone(payload.TraceTransfers, payload.Validation);
        Block? parentBlock = env.BlockTree.FindBlock(parent.Number);
        if (parentBlock is not null)
        {
            env.BlockTree.UpdateMainChain(new[] { parentBlock }, true, true);
            env.BlockTree.UpdateHeadBlock(parentBlock.Hash!);
        }

        IBlockProcessor? processor = env.GetProcessor();
        BlockStateCall<TransactionWithSourceDetails>? firstBlock = payload.BlockStateCalls?.FirstOrDefault();
        if (firstBlock?.BlockOverrides?.Number is > 0 and < long.MaxValue)
        {
            BlockHeader? searchResult = env.BlockTree.FindHeader((long)firstBlock.BlockOverrides.Number);
            if (searchResult is not null)
            {
                parent = searchResult;
            }
        }

        if (payload.BlockStateCalls is not null)
        {
            Dictionary<Address, UInt256> nonceCache = new();
            List<Block> suggestedBlocks = new();

            foreach (BlockStateCall<TransactionWithSourceDetails> callInputBlock in payload.BlockStateCalls)
            {
                BlockHeader callHeader = callInputBlock.BlockOverrides is not null
                    ? callInputBlock.BlockOverrides.GetBlockHeader(parent, _blocksConfig)
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
                        BaseFeePerGas = BaseFeeCalculator.Calculate(parent, _specProvider.GetSpec(parent)),
                        MixHash = parent.MixHash,
                        IsPostMerge = parent.Difficulty == 0
                    };

                UpdateStateByModifyingAccounts(callHeader, callInputBlock, env);

                using IReadOnlyTransactionProcessor? readOnlyTransactionProcessor = env.Build(env.StateProvider.StateRoot!);
                GasEstimator gasEstimator = new(readOnlyTransactionProcessor, env.StateProvider,
                    _specProvider, _blocksConfig);

                long EstimateGas(Transaction transaction)
                {
                    EstimateGasTracer estimateGasTracer = new();
                    return gasEstimator.Estimate(transaction, callHeader, estimateGasTracer);
                }


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

                    if (!transactionDetails.HadGasLimitInRequest)
                    {
                        long limit = EstimateGas(transaction);
                        transaction.GasLimit = limit;
                        transaction.GasLimit = (long)callHeader.BaseFeePerGas
                                               + Math.Max(IntrinsicGasCalculator.Calculate(transaction, spec) + 1,
                                                   transaction.GasLimit);

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
                IReleaseSpec? spec = _specProvider.GetSpec(parent);
                IEnumerable<Transaction> transactions = callInputBlock.Calls?.Select(t => SetTxHashAndMissingDefaults(t, spec)) ?? Array.Empty<Transaction>();
                Block? currentBlock = new(callHeader, transactions, Array.Empty<BlockHeader>());
                currentBlock.Header.Hash = currentBlock.Header.CalculateHash();

                ProcessingOptions processingFlags = _multicallProcessingOptions;

                if (!payload.Validation)
                {
                    processingFlags |= ProcessingOptions.NoValidation;
                }

                suggestedBlocks.Clear();
                suggestedBlocks.Add(currentBlock);
                env.StateProvider.RecalculateStateRoot();

                Block[]? currentBlocks = processor.Process(env.StateProvider.StateRoot, suggestedBlocks, processingFlags, tracer);
                Block? processedBlock = currentBlocks[0];
                parent = processedBlock.Header;
            }
        }

        return (true, "");
    }
}
