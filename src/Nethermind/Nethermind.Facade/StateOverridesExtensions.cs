// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Facade;

public static class StateOverridesExtensions
{
    public static void ApplyStateOverrides(
        this IWorldState state,
        OverridableCodeInfoRepository overridableCodeInfoRepository,
        Dictionary<Address, AccountOverride>? overrides,
        IReleaseSpec spec,
        long blockNumber)
    {
        if (overrides is not null)
        {
            foreach ((Address address, AccountOverride accountOverride) in overrides)
            {
                if (!state.TryGetAccount(address, out AccountStruct account))
                {
                    state.CreateAccount(address, accountOverride.Balance ?? UInt256.Zero, accountOverride.Nonce ?? UInt256.Zero);
                }
                else
                {
                    state.UpdateBalance(spec, account, accountOverride, address);
                    state.UpdateNonce(account, accountOverride, address);
                }

                state.UpdateCode(overridableCodeInfoRepository, spec, accountOverride, address);
                state.UpdateState(accountOverride, address);
            }
        }

        state.Commit(spec);
        state.CommitTree(blockNumber);
        state.RecalculateStateRoot();
    }

    private static bool TryGetAccount(this IWorldState stateProvider, Address address, out AccountStruct account)
    {
        try
        {
            account = stateProvider.GetAccount(address);
        }
        catch (TrieException)
        {
            account = new AccountStruct();
        }

        return !account.IsTotallyEmpty;
    }

    private static void UpdateState(this IWorldState stateProvider, AccountOverride accountOverride, Address address)
    {
        void ApplyState(Dictionary<UInt256, Hash256> diff)
        {
            foreach ((UInt256 index, Hash256 value) in diff)
            {
                stateProvider.Set(new StorageCell(address, index), value.Bytes.WithoutLeadingZeros().ToArray());
            }
        }

        if (accountOverride.State is not null)
        {
            stateProvider.ClearStorage(address);
            ApplyState(accountOverride.State);
        }
        else if (accountOverride.StateDiff is not null)
        {
            ApplyState(accountOverride.StateDiff);
        }
    }

    private static void UpdateCode(
        this IWorldState stateProvider,
        OverridableCodeInfoRepository overridableCodeInfoRepository,
        IReleaseSpec currentSpec,
        AccountOverride accountOverride,
        Address address)
    {
        if (accountOverride.Code is not null)
        {
            overridableCodeInfoRepository.SetCodeOverwrite(
                stateProvider,
                currentSpec,
                address,
                new CodeInfo(accountOverride.Code),
                accountOverride.MovePrecompileToAddress);
        }
    }

    private static void UpdateNonce(
        this IWorldState stateProvider,
        in AccountStruct account,
        AccountOverride accountOverride,
        Address address)
    {
        if (accountOverride.Nonce is not null)
        {
            UInt256 nonce = account.Nonce;
            UInt256 newNonce = accountOverride.Nonce.Value;
            if (nonce > newNonce)
            {
                stateProvider.DecrementNonce(address, nonce - newNonce);
            }
            else if (nonce < accountOverride.Nonce)
            {
                stateProvider.IncrementNonce(address, newNonce - nonce);
            }
        }
    }

    private static void UpdateBalance(
        this IWorldState stateProvider,
        IReleaseSpec spec,
        in AccountStruct account,
        AccountOverride accountOverride,
        Address address)
    {
        if (accountOverride.Balance is not null)
        {
            UInt256 balance = account.Balance;
            UInt256 newBalance = accountOverride.Balance.Value;
            if (balance > newBalance)
            {
                stateProvider.SubtractFromBalance(address, balance - newBalance, spec);
            }
            else if (balance < newBalance)
            {
                stateProvider.AddToBalance(address, newBalance - balance, spec);
            }
        }
    }
}
