// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.State;

namespace Nethermind.Flashbots.Modules.Rbuilder;

public class RbuilderRpcModule(IBlockFinder blockFinder, ISpecProvider specProvider, IWorldStateManager worldStateManager)
    : IRbuilderRpcModule
{

    private readonly ObjectPool<IOverridableWorldScope> _overridableWorldScopePool = new DefaultObjectPool<IOverridableWorldScope>(new PooledIWorldStatePolicy(worldStateManager));

    public ResultWrapper<byte[]?> rbuilder_getCodeByHash(Hash256 hash)
    {
        return ResultWrapper<byte[]?>.Success(worldStateManager.GlobalStateReader.GetCode(hash));
    }

    public ResultWrapper<Hash256> rbuilder_calculateStateRoot(BlockParameter blockParam, IDictionary<Address, AccountChange> accountDiff)
    {
        BlockHeader? blockHeader = blockFinder.FindHeader(blockParam);
        if (blockHeader is null)
        {
            return ResultWrapper<Hash256>.Fail("Block not found", ErrorCodes.ResourceNotFound);
        }

        IOverridableWorldScope worldScope = _overridableWorldScopePool.Get();
        using var _ = worldScope.BeginScope(blockHeader);
        try
        {
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
            worldState.RecalculateStateRoot();
            return ResultWrapper<Hash256>.Success(worldState.StateRoot);
        }
        finally
        {
            _overridableWorldScopePool.Return(worldScope);
        }
    }


    public ResultWrapper<AccountState?> rbuilder_getAccount(Address address, BlockParameter block)
    {
        BlockHeader? blockHeader = blockFinder.FindHeader(block);
        if (blockHeader is null)
        {
            return ResultWrapper<AccountState?>.Fail("Block not found", ErrorCodes.ResourceNotFound);
        }

        if (worldStateManager.GlobalStateReader.TryGetAccount(blockHeader, address,
                out AccountStruct account))
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

    private class PooledIWorldStatePolicy(IWorldStateManager worldStateManager)
        : IPooledObjectPolicy<IOverridableWorldScope>
    {
        public IOverridableWorldScope Create()
        {
            return worldStateManager.CreateOverridableWorldScope();
        }

        public bool Return(IOverridableWorldScope obj)
        {
            return true;
        }
    }
}
