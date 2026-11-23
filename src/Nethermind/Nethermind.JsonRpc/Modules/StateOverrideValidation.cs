// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Nethermind.JsonRpc.Modules;

internal static class StateOverrideValidation
{
    /// <summary>
    /// Returns the error for conflicting storage fields.
    /// </summary>
    public static string? GetStorageConflictError(Address address, AccountOverride? accountOverride)
    {
        if (accountOverride is { State: not null, StateDiff: not null })
            return $"account {address.ToString(withEip55Checksum: true)} has both 'state' and 'stateDiff'";

        return null;
    }

    /// <summary>
    /// Returns the storage error for the lowest invalid address in <paramref name="overrides"/>.
    /// </summary>
    /// <remarks>Geth applies overrides in ascending address order and stops at the first conflict.</remarks>
    public static string? GetStorageConflictError(Dictionary<Address, AccountOverride>? overrides)
    {
        if (overrides is null) return null;

        Address? lowest = null;
        string? error = null;
        foreach ((Address address, AccountOverride? accountOverride) in overrides)
        {
            if ((lowest is null || address.CompareTo(lowest) < 0) && GetStorageConflictError(address, accountOverride) is { } storageError)
            {
                lowest = address;
                error = storageError;
            }
        }

        return error;
    }

    /// <summary>
    /// Returns the error of the lowest address in <paramref name="overrides"/> with an invalid precompile move or a
    /// invalid storage override, or <see langword="null"/> if there is none.
    /// </summary>
    /// <remarks>Geth applies overrides in ascending address order and checks the move of an account before its storage.</remarks>
    public static string? GetError(Dictionary<Address, AccountOverride>? overrides, IReleaseSpec spec)
    {
        if (overrides is null) return null;

        Address[] addresses = [.. overrides.Keys];
        Array.Sort(addresses);
        foreach (Address address in addresses)
        {
            AccountOverride? accountOverride = overrides[address];
            if (accountOverride?.MovePrecompileToAddress is { } destination
                && StateOverridesExtensions.GetPrecompileMoveError(address, destination, spec.IsPrecompile(address), overrides) is { } moveError)
                return moveError;

            if (GetStorageConflictError(address, accountOverride) is { } storageError)
                return storageError;
        }

        return null;
    }
}
