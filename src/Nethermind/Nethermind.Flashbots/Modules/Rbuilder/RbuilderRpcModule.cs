// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Flashbots.Data;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.State;

namespace Nethermind.Flashbots.Modules.Rbuilder;

public class RbuilderRpcModule(
    IBlockFinder blockFinder,
    ISpecProvider specProvider,
    IShareableTxProcessorSource txProcessorSource,
    IStateReader stateReader,
    IBlockchainBridge blockchainBridge)
    : IRbuilderRpcModule
{

    public ResultWrapper<byte[]?> rbuilder_getCodeByHash(Hash256 hash)
    {
        return ResultWrapper<byte[]?>.Success(stateReader.GetCode(hash));
    }

    public ResultWrapper<Hash256> rbuilder_calculateStateRoot(BlockParameter blockParam, IDictionary<Address, AccountChange> accountDiff)
    {
        BlockHeader? blockHeader = blockFinder.FindHeader(blockParam);
        if (blockHeader is null)
        {
            return ResultWrapper<Hash256>.Fail("Block not found", ErrorCodes.ResourceNotFound);
        }

        using IReadOnlyTxProcessingScope worldScope = txProcessorSource.Build(blockHeader);
        IWorldState worldState = worldScope.WorldState;
        IReleaseSpec releaseSpec = specProvider.GetSpec(blockHeader);

        foreach (KeyValuePair<Address, AccountChange> kv in accountDiff)
        {
            Address address = kv.Key;
            AccountChange accountChange = kv.Value;

            if (accountChange.SelfDestructed)
            {
                worldState.DeleteAccount(address);
            }

            bool hasAccountChange = accountChange.Balance is not null
                                    || accountChange.Nonce is not null
                                    || accountChange.CodeHash is not null
                                    || accountChange.ChangedSlots?.Count > 0;
            if (!hasAccountChange) continue;

            if (worldState.TryGetAccount(address, out AccountStruct account))
            {
                // IWorldState does not actually have set nonce or set balance.
                // Set, its either this or changing `IWorldState` which is somewhat risky.
                if (accountChange.Nonce is not null)
                {
                    worldState.SetNonce(address, accountChange.Nonce.Value);
                }

                if (accountChange.Balance is not null)
                {
                    UInt256 originalBalance = account.Balance;
                    if (accountChange.Balance.Value > originalBalance)
                    {
                        worldState.AddToBalance(address, accountChange.Balance.Value - originalBalance, releaseSpec);
                    }
                    else if (accountChange.Balance.Value == originalBalance)
                    {
                    }
                    else
                    {
                        worldState.SubtractFromBalance(address, originalBalance - accountChange.Balance.Value, releaseSpec);
                    }
                }
            }
            else
            {
                worldState.CreateAccountIfNotExists(address, accountChange.Balance ?? 0, accountChange.Nonce ?? 0);
            }

            if (accountChange.CodeHash is not null)
            {
                // Note, this also set CodeDb, but since this is a read only world state, it should do nothing.
                worldState.InsertCode(address, accountChange.CodeHash, Array.Empty<byte>(), releaseSpec, false);
            }

            if (accountChange.ChangedSlots is not null)
            {
                foreach (KeyValuePair<UInt256, UInt256> changedSlot in accountChange.ChangedSlots)
                {
                    ReadOnlySpan<byte> bytes = changedSlot.Value.ToBigEndian().WithoutLeadingZeros();
                    worldState.Set(new StorageCell(address, changedSlot.Key), bytes.ToArray());
                }
            }
        }

        worldState.Commit(releaseSpec);
        worldState.CommitTree(blockHeader.Number + 1);
        return ResultWrapper<Hash256>.Success(worldState.StateRoot);
    }


    public ResultWrapper<AccountState?> rbuilder_getAccount(Address address, BlockParameter block)
    {
        BlockHeader? blockHeader = blockFinder.FindHeader(block);
        if (blockHeader is null)
        {
            return ResultWrapper<AccountState?>.Fail("Block not found", ErrorCodes.ResourceNotFound);
        }

        if (stateReader.TryGetAccount(blockHeader, address, out AccountStruct account))
        {
            return ResultWrapper<AccountState?>.Success(new AccountState(account.Nonce, account.Balance,
                account.CodeHash));
        }

        return ResultWrapper<AccountState?>.Success(null);
    }

    public ResultWrapper<Hash256?> rbuilder_getBlockHash(BlockParameter block)
    {

        BlockHeader? blockHeader = blockFinder.FindHeader(block);
        return ResultWrapper<Hash256?>.Success(blockHeader?.Hash);
    }

    public ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> rbuilder_transact(
        RevmTransaction revmTransaction,
        BundleState bundleState)
    {
        // 1. Apply diff on worldState
        // 2. Construct correct objects and call SimulateTxExecutor.Execute / collect traces
        // 3. Return the result

        // TODO: Too many conversions here
        TransactionForRpc transactionForRpc = TransactionForRpc.FromTransaction(revmTransaction.ToTransaction());

        BlockParameter blockParameter = BlockParameter.Latest;
        BlockHeader? blockHeader = blockFinder.FindHeader(blockParameter);
        if (blockHeader is null)
        {
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>>.Fail("Latest Block not available", ErrorCodes.ResourceNotFound);
        }

        // TODO: Apply `(address, accountChange)` changes
        // foreach (var (address, bundleAccount) in bundleState.State)
        // {
        //     var accountChange = bundleAccount.ToAccountChange();
        // }

        var executor = new SimulateTxExecutor<ParityLikeTxTrace>(
            blockchainBridge,
            blockFinder,
            new JsonRpcConfig(), // TODO: Inject
            new ParityStyleSimulateBlockTracerFactory(types: ParityTraceTypes.StateDiff));

        var payload = new SimulatePayload<TransactionForRpc>
        {
            BlockStateCalls =
            [
                new()
                {
                    Calls = [transactionForRpc],
                }
            ],
            // TODO: We might not need all of these
            TraceTransfers = true,
            Validation = true,
            ReturnFullTransactionObjects = true,
            ReturnFullTransactions = true
        };

        // TODO: Can we use the optional `stateOverride` parameter instead of manually applying changes?
        var result = executor.Execute(payload, blockParameter);

        return result;
    }
}

/*
TODO:

- What is the difference between `Evm.AccountOverride` and `AccountChange`? (ask @asdacap)
*/
