// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.RBuilder;

public class RbuilderRpcModule(
    IBlockFinder blockFinder,
    ISpecProvider specProvider,
    IWorldStateManager worldStateManager) : IRbuilderRpcModule
{
    private readonly ObjectPool<IOverridableWorldScope> _overridableWorldScopePool =
        new DefaultObjectPool<IOverridableWorldScope>(new PooledIWorldStatePolicy(worldStateManager));

    public ResultWrapper<byte[]> rbuilder_getCodeByHash(Hash256 hash)
    {
        return ResultWrapper<byte[]>.Success(worldStateManager.GlobalStateReader.GetCode(hash));
    }

    public ResultWrapper<Hash256> rbuilder_calculateStateRoot(BlockParameter blockParam,
        IDictionary<Address, AccountChange> accountDiff)
    {
        BlockHeader? blockHeader = blockFinder.FindHeader(blockParam);
        if (blockHeader is null) return ResultWrapper<Hash256>.Fail("Block not found");

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

                // Console.WriteLine($"{address} Balance {accountChange.Balance}");
                // Console.WriteLine($"{address} Nonce {accountChange.Nonce}");
                // Console.WriteLine($"{address} Storage diff {accountChange.ChangedSlots?.Count}");

                if (accountChange.SelfDestructed) worldState.DeleteAccount(address);

                var hasAccountChange = accountChange.Balance is not null
                                       || accountChange.Nonce is not null
                                       || accountChange.Code is not null
                                       || accountChange.ChangedSlots?.Count > 0;
                //   if (!hasAccountChange) continue;

                if (worldState.TryGetAccount(address, out AccountStruct account))
                {
                    // IWorldState does not actually have set nonce or set balance.
                    // Set, its either this or changing `IWorldState` which is somewhat risky.
                    if (accountChange.Nonce is not null)
                    {
                        UInt256 originalNonce = account.Nonce;
                        if (accountChange.Nonce.Value > originalNonce)
                            worldState.IncrementNonce(address, accountChange.Nonce.Value - originalNonce);
                        else if (accountChange.Nonce.Value == originalNonce)
                        {
                        }
                        else
                        {
                            Console.WriteLine($"DECREMENT!!!: {address} Nonce {accountChange.Nonce}");
                            worldState.DecrementNonce(address, originalNonce - accountChange.Nonce.Value);
                        }
                    }

                    if (accountChange.Balance is not null)
                    {
                        UInt256 originalBalance = account.Balance;
                        if (accountChange.Balance.Value > originalBalance)
                            worldState.AddToBalance(address, accountChange.Balance.Value - originalBalance,
                                releaseSpec);
                        else if (accountChange.Balance.Value == originalBalance)
                        {
                        }
                        else
                            worldState.SubtractFromBalance(address, originalBalance - accountChange.Balance.Value,
                                releaseSpec);
                    }
                }
                else
                    worldState.CreateAccountIfNotExists(address, accountChange.Balance ?? 0, accountChange.Nonce ?? 0);

                ValueHash256 codeHash = worldState.GetCodeHash(address);
                var code = worldState.GetCode(codeHash);
                if (!codeHash.Equals(accountChange.CodeHash))
                {
                    Console.WriteLine($"NM code hash {codeHash}, rbuilder code hash {accountChange.CodeHash}");

                    if (accountChange.Code is not null)
                    {
                        if (accountChange.Code != code)
                        {
                            Console.WriteLine(
                                $"{codeHash}, {accountChange.CodeHash}: {BitConverter.ToString(accountChange.Code)}, {BitConverter.ToString(code)}");
                            worldState.InsertCode(address, accountChange.Code, releaseSpec);
                        }
                    }
                }


                if (accountChange.ChangedSlots is not null)
                {

                   var changedSlotsSorted = accountChange.ChangedSlots.OrderBy(kv => kv.Key).ToList();
                    foreach (KeyValuePair<UInt256, UInt256> changedSlot in changedSlotsSorted)
                    {
                        //ReadOnlySpan<byte> prevValue = worldState.Get(new StorageCell(address, changedSlot.Key));
                        //Console.WriteLine($"CHANGED SLOT {BitConverter.ToString(prevValue.ToArray())}: {BitConverter.ToString(changedSlot.Value.ToBigEndian())}");
                        worldState.Set(new StorageCell(address, changedSlot.Key), changedSlot.Value.ToBigEndian());
                    }
                }
            }

            worldState.Commit(releaseSpec);
            worldState.RecalculateStateRoot();

            foreach (KeyValuePair<Address,AccountChange> diff in accountDiff)
            {
                Address address = diff.Key;
                AccountChange accountChange = diff.Value;


                if (worldState.TryGetAccount(address, out AccountStruct account))
                {
                   if (account.Balance != accountChange.Balance)
                   {
                       Console.WriteLine($"NM balance {account.Balance}, rbuilder balance {account.Balance}, address: {address}");
                   }

                   if (account.Nonce != accountChange.Nonce)
                   {
                       Console.WriteLine($"NM nonce {account.Nonce}, rbuilder nonce {account.Nonce}, address: {address}");
                   }

                   if (!account.CodeHash.Equals(accountChange.CodeHash))
                   {
                       Console.WriteLine($"NM code hash {account.CodeHash}, rbuilder code hash {account.CodeHash}, address: {address}");
                   }

                   foreach (KeyValuePair<UInt256,UInt256> slot in accountChange.ChangedSlots)
                   {

                        ReadOnlySpan<byte> nmVaule = worldState.Get(new StorageCell(address, slot.Key));
                        if (!nmVaule.ToArray().SequenceEqual(slot.Value.ToBigEndian()))
                        {
                            Console.WriteLine($"NM slot value {BitConverter.ToString(nmVaule.ToArray())}, rbuilder slot value {BitConverter.ToString(slot.Value.ToBigEndian())}, address: {address}");
                        }
                   }
                }
                else
                {
                    Console.WriteLine($"ACCOUNT NOT FOUND: {address}");
                }


            }

            return ResultWrapper<Hash256>.Success(worldState.StateRoot);
        }
        finally
        {
            _overridableWorldScopePool.Return(worldScope);
        }
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
            obj.WorldState.Reset();
            obj.WorldState.ResetOverrides();
            return true;
        }
    }
}
