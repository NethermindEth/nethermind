// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;

namespace Nethermind.JsonRpc.Modules;

internal static class StateOverrideValidation
{
    /// <summary>
    /// Returns the error for an account override that sets both <c>state</c> and <c>stateDiff</c>, or
    /// <see langword="null"/> if it sets at most one of them.
    /// </summary>
    public static string? GetStorageConflictError(Address address, AccountOverride? accountOverride) =>
        accountOverride is { State: not null, StateDiff: not null }
            ? $"account {address.ToString(withEip55Checksum: true)} has both 'state' and 'stateDiff'"
            : null;

    /// <summary>
    /// Returns the storage conflict error of the lowest conflicting address in <paramref name="overrides"/>, or
    /// <see langword="null"/> if no account override sets both <c>state</c> and <c>stateDiff</c>.
    /// </summary>
    /// <remarks>Geth applies overrides in ascending address order and stops at the first conflict.</remarks>
    public static string? GetStorageConflictError(Dictionary<Address, AccountOverride>? overrides)
    {
        if (overrides is null) return null;

        Address? lowest = null;
        foreach ((Address address, AccountOverride? accountOverride) in overrides)
        {
            if (accountOverride is { State: not null, StateDiff: not null } && (lowest is null || address.CompareTo(lowest) < 0))
                lowest = address;
        }

        return lowest is null ? null : GetStorageConflictError(lowest, overrides[lowest]);
    }
}
