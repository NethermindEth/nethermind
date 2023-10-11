// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;
using Nethermind.State;
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

    void UpdateStateByModifyingAccounts(BlockHeader blockHeader, BlockStateCall<Transaction>? blockStateCall, MultiCallReadOnlyBlocksProcessingEnv env)
    {
        IReleaseSpec? currentSpec = env.SpecProvider.GetSpec(blockHeader);
        if (blockStateCall!.StateOverrides is not null)
        {
            ModifyAccounts(blockStateCall.StateOverrides, env.StateProvider, currentSpec);
        }

        env.StateProvider.Commit(currentSpec);
        env.StateProvider.CommitTree(blockHeader.Number);
        env.StateProvider.RecalculateStateRoot();
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

        Dictionary<Address, UInt256> nonceCache = new();

        List<Block> suggestedBlocks = new();

        if (payload.BlockStateCalls is not null)
        {
            foreach (BlockStateCall<Transaction>? callInputBlock in payload.BlockStateCalls)
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

    private bool TryGetAccount(IWorldState stateProvider, Address address, out Account account)
    {
        bool accExists = false;
        try
        {
            accExists = stateProvider.AccountExists(address);
            account = stateProvider.GetAccount(address);
        }
        catch (Exception)
        {
            account = Account.TotallyEmpty;
        }

        return accExists;
    }

    private void ModifyAccounts(Dictionary<Address, AccountOverride> stateOverrides, IWorldState stateProvider, IReleaseSpec currentSpec)
    {
        foreach (KeyValuePair<Address, AccountOverride> overrideData in stateOverrides)
        {
            Address address = overrideData.Key;
            AccountOverride? accountOverride = overrideData.Value;

            UInt256 balance = 0;
            if (accountOverride.Balance is not null)
            {
                balance = accountOverride.Balance.Value;
            }

            UInt256 nonce = 0;
            if (accountOverride.Nonce is not null)
            {
                nonce = accountOverride.Nonce.Value;
            }

            if (!TryGetAccount(stateProvider, address, out Account? acc))
            {
                stateProvider.CreateAccount(address, balance, nonce);
            }
            else
            {
                UpdateBalance(stateProvider, currentSpec, acc, accountOverride, balance, address);
                UpdateNonce(stateProvider, acc, accountOverride, nonce, address);
            }

            UpdateCode(stateProvider, currentSpec, accountOverride, address);
            UpdateState(stateProvider, accountOverride, address);
        }
    }

    private static void UpdateState(IWorldState stateProvider, AccountOverride accountOverride, Address address)
    {
        if (accountOverride.State is not null)
        {
            stateProvider.ClearStorage(address);
            foreach (KeyValuePair<UInt256, ValueKeccak> storage in accountOverride.State)
                stateProvider.Set(new StorageCell(address, storage.Key), storage.Value.Bytes.WithoutLeadingZeros().ToArray());
        }

        if (accountOverride.StateDiff is not null)
        {
            foreach (KeyValuePair<UInt256, ValueKeccak> storage in accountOverride.StateDiff)
                stateProvider.Set(new StorageCell(address, storage.Key), storage.Value.Bytes.WithoutLeadingZeros().ToArray());
        }
    }

    private void UpdateCode(IWorldState stateProvider, IReleaseSpec currentSpec, AccountOverride? accountOverride,
        Address address)
    {
        if (accountOverride?.Code is not null)
        {
            _multiCallProcessingEnv.CodeInfoRepository.SetCodeOverwrite(stateProvider, currentSpec, address,
                new CodeInfo(accountOverride.Code), accountOverride.MovePrecompileToAddress);
        }
    }

    private static void UpdateNonce(IWorldState stateProvider, Account acc, AccountOverride accountOverride, UInt256 nonce, Address address)
    {
        UInt256 accNonce = acc.Nonce;
        if (accountOverride.Nonce != null && accNonce > nonce)
        {
            UInt256 iters = accNonce - nonce;
            for (UInt256 i = 0; i < iters; i++)
            {
                stateProvider.DecrementNonce(address);
            }
        }
        else if (accountOverride.Nonce != null && accNonce < accountOverride.Nonce)
        {
            UInt256 iters = nonce - accNonce;
            for (UInt256 i = 0; i < iters; i++)
            {
                stateProvider.IncrementNonce(address);
            }
        }
    }

    private static void UpdateBalance(IWorldState stateProvider, IReleaseSpec currentSpec, Account acc, AccountOverride accountOverride, UInt256 balance, Address address)
    {
        if (accountOverride.Balance is not null)
        {
            UInt256 accBalance = acc.Balance;
            if (accBalance > balance)
            {
                stateProvider.SubtractFromBalance(address, accBalance - balance, currentSpec);
            }
            else if (accBalance < balance)
            {
                stateProvider.AddToBalance(address, balance - accBalance, currentSpec);
            }
        }
    }
}
