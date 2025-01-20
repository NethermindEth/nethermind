// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.RBuilder;

public class RbuilderRpcModule(IBlockFinder blockFinder, ISpecProvider specProvider, IWorldStateManager worldStateManager): IRbuilderRpcModule
{
    private readonly ObjectPool<IOverridableWorldScope> _overridableWorldScopePool = new DefaultObjectPool<IOverridableWorldScope>(new PooledIWorldStatePolicy(worldStateManager));

    public ResultWrapper<byte[]> rbuilder_getCodeByHash(Hash256 hash)
    {
        return ResultWrapper<byte[]>.Success(worldStateManager.GlobalStateReader.GetCode(hash));
    }

    public ResultWrapper<Hash256> rbuilder_calculateStateRoot(BlockParameter blockParam, IDictionary<Address, AccountChange> accountDiff)
    {
        BlockHeader? blockHeader = blockFinder.FindHeader(blockParam);
        if (blockHeader is null)
        {
            return ResultWrapper<Hash256>.Fail("Block not found");
        }

        IOverridableWorldScope worldScope = _overridableWorldScopePool.Get();
        try
        {
            IWorldState worldState = worldScope.WorldState;
            IReleaseSpec releaseSpec = specProvider.GetSpec(blockHeader);
            worldState.StateRoot = blockHeader.StateRoot!;

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
                                        || accountChange.Code is not null
                                        || accountChange.ChangedSlots?.Count > 0;
                if (!hasAccountChange) continue;

                if (worldState.TryGetAccount(address, out AccountStruct account))
                {
                    // IWorldState does not actually have set nonce or set balance.
                    // Set, its either this or changing `IWorldState` which is somewhat risky.
                    if (accountChange.Nonce is not null)
                    {
                        UInt256 originalNonce = account.Nonce;
                        if (accountChange.Nonce.Value > originalNonce)
                        {
                            worldState.IncrementNonce(address, accountChange.Nonce.Value - originalNonce);
                        }
                        else if (accountChange.Nonce.Value == originalNonce)
                        {
                        }
                        else
                        {
                            worldState.DecrementNonce(address, originalNonce - accountChange.Nonce.Value);
                        }
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

                if (accountChange.Code is not null)
                {
                    worldState.InsertCode(address, accountChange.Code, releaseSpec);
                }

                if (accountChange.ChangedSlots is not null)
                {
                    foreach (KeyValuePair<UInt256, Hash256> changedSlot in accountChange.ChangedSlots)
                    {
                        worldState.Set(new StorageCell(address, changedSlot.Key), changedSlot.Value.BytesToArray());
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

    private class PooledIWorldStatePolicy(IWorldStateManager worldStateManager): IPooledObjectPolicy<IOverridableWorldScope>
    {
        public IOverridableWorldScope Create()
        {
            return worldStateManager.CreateOverridableWorldScope();
        }

        public bool Return(IOverridableWorldScope obj)
        {
            obj.WorldState.Reset();
            obj.WorldState.ResetOverrides();
            return true;
        }
    }
}
