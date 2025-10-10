// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

public static class StateOverridesExtensions
{

    public static void ApplyStateOverridesNoCommit(
        this IWorldState state,
        IOverridableCodeInfoRepository overridableCodeInfoRepository,
        Dictionary<Address, AccountOverride>? overrides,
        IReleaseSpec spec)
    {
        if (overrides is not null)
        {
            overridableCodeInfoRepository.ResetPrecompileOverrides();
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
    }

    public static void ApplyStateOverrides(
        this IWorldState state,
        IOverridableCodeInfoRepository overridableCodeInfoRepository,
        Dictionary<Address, AccountOverride>? overrides,
        IReleaseSpec spec,
        long blockNumber)
    {
        state.ApplyStateOverridesNoCommit(overridableCodeInfoRepository, overrides, spec);

        state.Commit(spec);
        state.CommitTree(blockNumber);
        state.RecalculateStateRoot();
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
        IOverridableCodeInfoRepository overridableCodeInfoRepository,
        IReleaseSpec currentSpec,
        AccountOverride accountOverride,
        Address address)
    {
        if (accountOverride.MovePrecompileToAddress is not null)
        {
            if (!overridableCodeInfoRepository.GetCachedCodeInfo(address, currentSpec).IsPrecompile)
            {
                throw new ArgumentException($"Account {address} is not a precompile");
            }

            overridableCodeInfoRepository.MovePrecompile(
                currentSpec,
                address,
                accountOverride.MovePrecompileToAddress);
        }

        if (accountOverride.Code is not null)
        {
            stateProvider.InsertCode(address, accountOverride.Code, currentSpec);

            overridableCodeInfoRepository.SetCodeOverwrite(
                currentSpec,
                address,
                new CodeInfo(accountOverride.Code));
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
                stateProvider.AddToBalanceAndCreateIfNotExists(address, newBalance - balance, spec);
            }
        }
    }
}
