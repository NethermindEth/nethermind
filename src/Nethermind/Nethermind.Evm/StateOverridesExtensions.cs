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
    private static readonly UInt256 MaxNonce = ulong.MaxValue;

    public static void ApplyStateOverridesNoCommit(
        this IWorldState state,
        IOverridableCodeInfoRepository overridableCodeInfoRepository,
        Dictionary<Address, AccountOverride>? overrides,
        IReleaseSpec spec)
    {
        // As in geth, each simulated block starts from the spec's precompiles, even one without overrides.
        overridableCodeInfoRepository.ResetPrecompileOverrides(spec);
        if (overrides is not null)
        {
            foreach ((Address address, AccountOverride? value) in overrides)
            {
                AccountOverride accountOverride = value ?? new AccountOverride();
                if (accountOverride.Nonce is not null && accountOverride.Nonce.Value > MaxNonce)
                    throw new ArgumentException($"Nonce override {accountOverride.Nonce.Value} exceeds the maximum supported value ({MaxNonce})");

                if (!state.TryGetAccount(address, out AccountStruct account))
                {
                    if (accountOverride.HasStateChanges)
                    {
                        state.CreateAccount(address, accountOverride.Balance ?? UInt256.Zero, accountOverride.Nonce ?? 0);
                    }
                }
                else
                {
                    state.UpdateBalance(spec, account, accountOverride, address);
                    state.UpdateNonce(account, accountOverride, address);
                }

                state.UpdateCode(overridableCodeInfoRepository, spec, overrides, accountOverride, address);
                state.UpdateState(accountOverride, address);
            }
        }
    }

    /// <summary>Applies <paramref name="overrides"/> to <paramref name="state"/> and commits them.</summary>
    /// <remarks>
    /// With <paramref name="merkleize"/> unset, the commit leaves storage roots, the trie at <paramref name="blockNumber"/>
    /// and the state root alone. The overrides are then visible only through <paramref name="state"/>, which suits a call
    /// that reads the overridden state and never resolves it through a root.
    /// </remarks>
    public static void ApplyStateOverrides(
        this IWorldState state,
        IOverridableCodeInfoRepository overridableCodeInfoRepository,
        Dictionary<Address, AccountOverride>? overrides,
        IReleaseSpec spec,
        ulong blockNumber,
        bool merkleize = true)
    {
        spec = spec.WithoutEip158();
        state.ApplyStateOverridesNoCommit(overridableCodeInfoRepository, overrides, spec);
        state.Commit(spec, commitRoots: merkleize);
        if (!merkleize) return;

        state.CommitTree(blockNumber);
        state.RecalculateStateRoot();
    }

    private static void UpdateState(this IWorldState stateProvider, AccountOverride accountOverride, Address address)
    {
        void ApplyState(Dictionary<UInt256, Hash256> diff)
        {
            foreach ((UInt256 index, Hash256 value) in diff)
            {
                stateProvider.Set(new StorageCell(address, index), value.ToUInt256());
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

    /// <summary>
    /// Returns the error for an invalid precompile move, or <see langword="null"/> if the move is valid.
    /// </summary>
    /// <param name="source">The account to move.</param>
    /// <param name="destination">The account to move the precompile to.</param>
    /// <param name="isPrecompile">Whether the caller's code repository resolves the source as a precompile.</param>
    /// <param name="overrides">The account overrides to apply.</param>
    public static string? GetPrecompileMoveError(Address source, Address destination, bool isPrecompile, Dictionary<Address, AccountOverride> overrides) =>
        !isPrecompile ? $"Account {source} is not a precompile"
        : overrides.ContainsKey(destination) ? $"account {destination} is already overridden"
        : null;

    private static void UpdateCode(
        this IWorldState stateProvider,
        IOverridableCodeInfoRepository overridableCodeInfoRepository,
        IReleaseSpec currentSpec,
        Dictionary<Address, AccountOverride> overrides,
        AccountOverride accountOverride,
        Address address)
    {
        if (accountOverride.MovePrecompileToAddress is not null)
        {
            string? error = GetPrecompileMoveError(address, accountOverride.MovePrecompileToAddress,
                overridableCodeInfoRepository.GetCachedCodeInfoNoDelegation(address, currentSpec).IsPrecompile, overrides);
            if (error is not null) throw new ArgumentException(error);

            overridableCodeInfoRepository.MovePrecompile(
                currentSpec,
                address,
                accountOverride.MovePrecompileToAddress);
        }

        if (accountOverride.Code is not null)
        {
            OverrideCodeCache.Resolve(accountOverride.Code, out ValueHash256 codeHash, out CodeInfo codeInfo);
            stateProvider.InsertCode(address, codeHash, accountOverride.Code, currentSpec);

            overridableCodeInfoRepository.SetCodeOverride(
                currentSpec,
                address,
                codeInfo);
        }
        else if (accountOverride.MovePrecompileToAddress is null && overridableCodeInfoRepository.GetPrecompile(address, currentSpec) is not null)
        {
            // As in geth, any override of a precompile's address turns it into an ordinary account.
            overridableCodeInfoRepository.SetCodeOverride(currentSpec, address, new CodeInfo(stateProvider.GetCode(address)));
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
            ulong nonce = account.Nonce;
            ulong newNonce = accountOverride.Nonce.Value;
            if (nonce > newNonce)
            {
                stateProvider.DecrementNonce(address, nonce - newNonce);
            }
            else if (nonce < newNonce)
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
