// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;

[assembly: InternalsVisibleTo("Nethermind.State.Flat.Test")]

namespace Nethermind.State;

internal interface IAccountBatchReader
{
    bool TryGetCachedAccount(Address address, out Account? account)
    {
        account = null;
        return false;
    }

    void GetAccounts(ReadOnlySpan<Address> addresses, Span<Account?> accounts);
}
