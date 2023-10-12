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
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.Facade.Multicall;

public class MulticallBridgeHelper
{
    private readonly MultiCallReadOnlyBlocksProcessingEnv _multiCallProcessingEnv;
    private readonly ISpecProvider _specProvider;
    private readonly IBlocksConfig _blocksConfig;

    private static readonly ProcessingOptions _multicallProcessingOptions = ProcessingOptions.ForceProcessing |
                                                                            ProcessingOptions.DoNotVerifyNonce |
                                                                            ProcessingOptions.IgnoreParentNotOnMainChain |
                                                                            ProcessingOptions.MarkAsProcessed |
                                                                            ProcessingOptions.StoreReceipts;

    public MulticallBridgeHelper(MultiCallReadOnlyBlocksProcessingEnv multiCallProcessingEnv, ISpecProvider specProvider, IBlocksConfig blocksConfig)
    {
        _multiCallProcessingEnv = multiCallProcessingEnv;
        _specProvider = specProvider;
        _blocksConfig = blocksConfig;
    }

    private void UpdateStateByModifyingAccounts(BlockHeader blockHeader, BlockStateCall<Transaction> blockStateCall, MultiCallReadOnlyBlocksProcessingEnv env)
    {
        IReleaseSpec currentSpec = env.SpecProvider.GetSpec(blockHeader);
        env.StateProvider.ApplyStateOverrides(_multiCallProcessingEnv.CodeInfoRepository, blockStateCall.StateOverrides, currentSpec, blockHeader.Number);
        blockHeader.StateRoot = env.StateProvider.StateRoot;
    }

    public (bool Success, string Error) TryMultiCallTrace(BlockHeader parent, MultiCallPayload<Transaction> payload, IBlockTracer tracer)
    {
        using MultiCallReadOnlyBlocksProcessingEnv? env = _multiCallProcessingEnv.Clone(payload.TraceTransfers);
        Block? parentBlock = env.BlockTree.FindBlock(parent.Number);
        if (parentBlock is not null)
        {
            env.BlockTree.UpdateMainChain(new[] { parentBlock }, true, true);
            env.BlockTree.UpdateHeadBlock(parentBlock.Hash!);
        }

        IBlockProcessor? processor = env.GetProcessor();
        BlockStateCall<Transaction>? firstBlock = payload.BlockStateCalls?.FirstOrDefault();
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

            foreach (BlockStateCall<Transaction> callInputBlock in payload.BlockStateCalls)
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

                Transaction SetTxHashAndMissingDefaults(Transaction transaction)
                {
                    transaction.SenderAddress ??= Address.Zero;
                    transaction.To ??= Address.Zero;
                    transaction.Data ??= Memory<byte>.Empty;

                    if (transaction.Nonce == 0)
                    {
                        try
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
                        catch (TrieException)
                        {
                            // ignore
                            // Transaction from unknown account
                        }
                    }

                    transaction.Hash = transaction.CalculateHash();
                    return transaction;
                }

                IEnumerable<Transaction> transactions = callInputBlock.Calls?.Select(SetTxHashAndMissingDefaults) ?? Array.Empty<Transaction>();
                Block? currentBlock = new(callHeader, transactions, Array.Empty<BlockHeader>());
                currentBlock.Header.Hash = currentBlock.Header.CalculateHash();

                ProcessingOptions processingFlags = _multicallProcessingOptions;

                if (!payload.Validation)
                {
                    processingFlags |= ProcessingOptions.NoValidation;
                }

                suggestedBlocks.Clear();
                suggestedBlocks.Add(currentBlock);

                Block[]? currentBlocks = processor.Process(env.StateProvider.StateRoot, suggestedBlocks, processingFlags, tracer);
                Block? processedBlock = currentBlocks[0];
                parent = processedBlock.Header;
            }
        }

        return (true, "");
    }
}
